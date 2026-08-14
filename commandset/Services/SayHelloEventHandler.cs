using Autodesk.Revit.UI;
using RevitMCPSDK.API.Interfaces;

namespace RevitMCPCommandSet.Services
{
    public class SayHelloEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
    {
        private readonly ManualResetEvent _resetEvent = new ManualResetEvent(false);

        public string Message { get; set; } = "Hello MCP!";

                /// <summary>
        /// Reset wait state before ExternalEvent.Raise. Must be called from the command before RaiseAndWaitForCompletion.
        /// </summary>
        public void Prepare()
        {
            _resetEvent.Reset();
        }
        public bool WaitForCompletion(int timeoutMilliseconds = 10000)
        {
            // Do not Reset here - SetParameters/Prepare already Reset before Raise.
            return _resetEvent.WaitOne(timeoutMilliseconds);
        }

        public void Execute(UIApplication app)
        {
            try
            {
                // No TaskDialog.Show: this runs inside an ExternalEvent during an
                // agent-driven turn, with nobody able to click it — the modal would
                // block the Revit UI thread and hang the whole chat until dismissed.
                System.Diagnostics.Trace.WriteLine($"[Revit MCP] say_hello: {Message}");
            }
            finally
            {
                _resetEvent.Set();
            }
        }

        public string GetName()
        {
            return "Say Hello";
        }
    }
}
