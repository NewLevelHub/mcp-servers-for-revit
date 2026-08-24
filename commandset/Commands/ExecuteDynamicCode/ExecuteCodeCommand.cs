using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPSDK.API.Base;

namespace RevitMCPCommandSet.Commands.ExecuteDynamicCode
{
    /// <summary>
    /// 处理代码执行的命令类
    /// </summary>
    public class ExecuteCodeCommand : ExternalEventCommandBase
    {
        private ExecuteCodeEventHandler _handler => (ExecuteCodeEventHandler)Handler;

        public override string CommandName => "send_code_to_revit";

        public ExecuteCodeCommand(UIApplication uiApp)
            : base(new ExecuteCodeEventHandler(), uiApp)
        {
        }

        public override object Execute(JObject parameters, string requestId)
        {
            try
            {
                // 参数验证
                if (!parameters.ContainsKey("code"))
                {
                    throw new ArgumentException("Missing required parameter: 'code'");
                }

                // 解析代码和参数
                string code = parameters["code"].Value<string>();
                JArray parametersArray = parameters["parameters"] as JArray;
                object[] executionParameters = parametersArray?.ToObject<object[]>() ?? Array.Empty<object>();
                string transactionMode = parameters["transactionMode"]?.Value<string>() ?? ExecuteCodeEventHandler.TransactionModeAuto;
                // REV-175 sandbox knobs — optional; SetExecutionParameters clamps both to sane bounds.
                int maxChangedElements = parameters["maxChangedElements"]?.Value<int>() ?? 0;
                int timeoutSeconds = parameters["timeoutSeconds"]?.Value<int>() ?? 0;

                // 设置执行参数
                _handler.SetExecutionParameters(code, executionParameters, transactionMode, maxChangedElements, timeoutSeconds);

                // The ExternalEvent wait has to stay comfortably above the sandbox's own timeout
                // (SandboxGuard), or we'd report "timed out" here while the UI thread is still
                // inside the loop, still working toward its own internal cutoff.
                var waitMs = Math.Max(60000, (_handler.EffectiveTimeoutSeconds + 30) * 1000);

                // 触发外部事件并等待完成
                if (RaiseAndWaitForCompletion(waitMs))
                {
                    return _handler.ResultInfo;
                }
                else
                {
                    throw new TimeoutException("代码执行超时");
                }
            }
            catch (Exception ex)
            {
                throw new Exception($"执行代码失败: {ex.Message}", ex);
            }
        }
    }
}
