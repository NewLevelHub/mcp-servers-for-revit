using System.IO;
using System.Reflection;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Newtonsoft.Json;
using RevitMCPSDK.API.Interfaces;

namespace RevitMCPCommandSet.Commands.ExecuteDynamicCode
{
    /// <summary>
    /// 处理代码执行的外部事件处理器
    /// </summary>
    public class ExecuteCodeEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
    {
        public const string TransactionModeAuto = "auto";
        public const string TransactionModeNone = "none";

        /// <summary>
        /// REV-175: run the snippet in a transaction that is ALWAYS rolled back, regardless of
        /// outcome. Use this to preview what generated code would do before letting it commit.
        /// </summary>
        public const string TransactionModeTrial = "trial";

        // REV-175 sandbox defaults/bounds. Not user-configurable beyond these clamps, so a
        // request can't disable the safety net by asking for an absurd budget.
        private const int DefaultMaxChangedElements = 500;
        private const int MaxAllowedChangedElements = 20000;
        private const int DefaultTimeoutSeconds = 10;
        private const int MaxAllowedTimeoutSeconds = 120;

        /// <summary>Exposed for tests — see <see cref="SandboxGuard" /> remarks for why this exists.</summary>
        public const long LoopIterationBudget = 300_000;

        // 代码执行参数
        private string _generatedCode;
        private object[] _executionParameters;
        private string _transactionMode = TransactionModeAuto;
        private int _maxChangedElements = DefaultMaxChangedElements;
        private int _timeoutSeconds = DefaultTimeoutSeconds;

        // 执行结果信息
        public ExecutionResultInfo ResultInfo { get; private set; }

        /// <summary>
        /// The (clamped) timeout this run will actually use — read after <see cref="SetExecutionParameters" />
        /// so the caller can size its own ExternalEvent wait comfortably above it.
        /// </summary>
        public int EffectiveTimeoutSeconds => _timeoutSeconds;

        // 状态同步对象
        public bool TaskCompleted { get; private set; }
        private readonly ManualResetEvent _resetEvent = new ManualResetEvent(false);

        // 设置要执行的代码和参数
        public void SetExecutionParameters(
            string code,
            object[] parameters = null,
            string transactionMode = TransactionModeAuto,
            int maxChangedElements = 0,
            int timeoutSeconds = 0)
        {
            _generatedCode = code;
            _executionParameters = parameters ?? Array.Empty<object>();
            _transactionMode = transactionMode switch
            {
                TransactionModeNone => TransactionModeNone,
                TransactionModeTrial => TransactionModeTrial,
                _ => TransactionModeAuto,
            };
            _maxChangedElements = Clamp(
                maxChangedElements > 0 ? maxChangedElements : DefaultMaxChangedElements,
                1, MaxAllowedChangedElements);
            _timeoutSeconds = Clamp(
                timeoutSeconds > 0 ? timeoutSeconds : DefaultTimeoutSeconds,
                1, MaxAllowedTimeoutSeconds);
            TaskCompleted = false;
            _resetEvent.Reset();
        }

        private static int Clamp(int value, int min, int max) => Math.Max(min, Math.Min(max, value));

        // 等待执行完成 - IWaitableExternalEventHandler接口实现
        public bool WaitForCompletion(int timeoutMilliseconds = 10000)
        {
            // Do not Reset here - SetParameters/Prepare already Reset before Raise.
            return _resetEvent.WaitOne(timeoutMilliseconds);
        }

        public void Execute(UIApplication app)
        {
            var isTrial = _transactionMode == TransactionModeTrial;
            var diff = ChangeIntent.Empty;
            ResultInfo = new ExecutionResultInfo { IsTrial = isTrial };

            try
            {
                var doc = app.ActiveUIDocument.Document;
                var recorder = new ChangeIntentRecorder(doc);
                var timeout = TimeSpan.FromSeconds(_timeoutSeconds);

                object result;
                if (_transactionMode == TransactionModeNone)
                {
                    // No transaction of ours to roll back — the snippet is expected to manage
                    // its own. The timeout/iteration/API guards still apply, but the "nothing
                    // reaches the document" guarantee below only holds for auto/trial.
                    try
                    {
                        result = CompileAndExecuteCode(_generatedCode, doc, _executionParameters, timeout);
                        diff = recorder.Diff();
                        EnforceLimit(diff);
                    }
                    catch
                    {
                        diff = recorder.Diff();
                        throw;
                    }
                }
                else
                {
                    var transactionName = isTrial ? "Проба AI-кода (REV-175)" : "执行AI代码";
                    using var transaction = new Transaction(doc, transactionName);
                    transaction.Start();
                    try
                    {
                        result = CompileAndExecuteCode(_generatedCode, doc, _executionParameters, timeout);
                        diff = recorder.Diff();
                        EnforceLimit(diff); // throws before we ever commit if the budget is blown

                        // Trial never commits, even on success — that's the whole point.
                        if (isTrial)
                            transaction.RollBack();
                        else
                            transaction.Commit();
                    }
                    catch
                    {
                        // Capture the diff before undoing it, so a failure still reports what the
                        // code was in the middle of doing (REV-175's "journal of intent").
                        diff = recorder.Diff();
                        if (transaction.HasStarted() && !transaction.HasEnded())
                            transaction.RollBack();
                        throw;
                    }
                }

                ResultInfo.Success = true;
                ResultInfo.Result = JsonConvert.SerializeObject(result);
                ResultInfo.TotalChangedElements = diff.TouchedCount;
                ResultInfo.IntentReport = diff.BuildReport();
            }
            catch (Exception ex)
            {
                ResultInfo.Success = false;
                ResultInfo.ErrorMessage = DescribeFailure(ex);
                ResultInfo.TotalChangedElements = diff.TouchedCount;
                ResultInfo.IntentReport = diff.BuildReport();
            }
            finally
            {
                TaskCompleted = true;
                _resetEvent.Set();
            }
        }

        /// <summary>Throws once the run has created/deleted more than the configured budget.</summary>
        private void EnforceLimit(ChangeIntent diff)
        {
            if (diff.TouchedCount > _maxChangedElements)
                throw new SandboxLimitExceededException(diff.TouchedCount, _maxChangedElements);
        }

        /// <summary>REV-175: turn the sandbox's own exceptions into a message an architect can act on.</summary>
        private static string DescribeFailure(Exception ex)
        {
            return ex switch
            {
                SandboxTimeoutException t =>
                    $"остановлено таймаутом: код не уложился в {t.Limit.TotalSeconds:0.#} с — похоже на бесконечный цикл.",
                SandboxLoopIterationLimitException li =>
                    $"остановлено лимитом итераций цикла: {li.Iterations} > {li.Max} — похоже на обход всей модели много раз подряд.",
                SandboxLimitExceededException l =>
                    $"остановлено лимитом: код затронул {l.Touched} элементов при лимите {l.Max}; ничего не применено.",
                SandboxSecurityException s =>
                    $"запрещённый API: {s.SymbolName} (файловая система, сеть и процессы недоступны из песочницы).",
                _ => $"执行失败: {ex.Message}",
            };
        }

        private object CompileAndExecuteCode(string code, Document doc, object[] parameters, TimeSpan timeout)
        {
            // 包装代码以规范入口点
            var wrappedCode = $@"
using System;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System.Collections.Generic;

namespace AIGeneratedCode
{{
    public static class CodeExecutor
    {{
        public static object Execute(Document document, object[] parameters)
        {{
            // 用户代码入口
            {code}
        }}
    }}
}}";

            var syntaxTree = CSharpSyntaxTree.ParseText(wrappedCode);

            // REV-175: inject a timeout/iteration check into every loop before the snippet is
            // compiled — see SandboxGuard/LoopGuardRewriter for why this stands in for a real
            // timeout (there's no thread here to abort).
            syntaxTree = LoopGuardRewriter.Apply(syntaxTree);

            // 添加必要的程序集引用（引用所有已加载的程序集）
            var references = AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
                .Select(a => MetadataReference.CreateFromFile(a.Location))
                .Cast<MetadataReference>()
                .ToList();

            // 编译代码
            var compilation = CSharpCompilation.Create(
                "AIGeneratedCode",
                syntaxTrees: new[] { syntaxTree },
                references: references,
                options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
            );

            // REV-175: block filesystem/network/process APIs before spending time on Emit.
            DangerousApiGuard.Validate(compilation.GetSemanticModel(syntaxTree), syntaxTree.GetRoot());

            using (var ms = new MemoryStream())
            {
                var result = compilation.Emit(ms);

                // 处理编译结果
                if (!result.Success)
                {
                    var errors = string.Join("\n", result.Diagnostics
                        .Where(d => d.Severity == DiagnosticSeverity.Error)
                        .Select(d => $"Line {d.Location.GetLineSpan().StartLinePosition.Line}: {d.GetMessage()}"));
                    throw new Exception($"代码编译错误:\n{errors}");
                }

                // 反射调用执行方法
                ms.Seek(0, SeekOrigin.Begin);
                var assembly = Assembly.Load(ms.ToArray());
                var executorType = assembly.GetType("AIGeneratedCode.CodeExecutor");
                var executeMethod = executorType.GetMethod("Execute");

                SandboxGuard.Begin(timeout, LoopIterationBudget);
                try
                {
                    return executeMethod.Invoke(null, new object[] { doc, parameters });
                }
                catch (TargetInvocationException tie) when (tie.InnerException != null)
                {
                    // Unwrap so SandboxTimeoutException/SandboxLimitExceededException reach
                    // DescribeFailure as themselves, not buried in a reflection wrapper.
                    throw tie.InnerException;
                }
                finally
                {
                    SandboxGuard.End();
                }
            }
        }

        public string GetName()
        {
            return "执行AI代码";
        }
    }

    // 执行结果数据结构
    public class ExecutionResultInfo
    {
        [JsonProperty("success")]
        public bool Success { get; set; }

        [JsonProperty("result")]
        public string Result { get; set; }

        [JsonProperty("errorMessage")]
        public string ErrorMessage { get; set; } = string.Empty;

        /// <summary>REV-175: true when this run's transaction was rolled back unconditionally.</summary>
        [JsonProperty("isTrial")]
        public bool IsTrial { get; set; }

        /// <summary>REV-175: human-readable (Russian) journal of what the code created/deleted.</summary>
        [JsonProperty("intentReport")]
        public string IntentReport { get; set; }

        /// <summary>REV-175: distinct elements created + deleted, for the caller's own display.</summary>
        [JsonProperty("totalChangedElements")]
        public int TotalChangedElements { get; set; }
    }
}
