using BabelDevirtualizer.Logger;
using dnlib.DotNet;

namespace BabelVMRestore.Logger
{
    internal class ModuleWriterLogger : ILogger
    {
        public bool IgnoresEvent(LoggerEvent loggerEvent)
        {
            return false;
        }

        public void Log(object sender, LoggerEvent loggerEvent, string format, params object[] args)
        {
            switch (loggerEvent)
            {

                case LoggerEvent.Verbose:
                case LoggerEvent.VeryVerbose:
                    ConsoleLogger.Verbose(format, args);
                    break;
                case LoggerEvent.Info:
                    ConsoleLogger.Info(format, args);
                    break;
                case LoggerEvent.Warning:
                    ConsoleLogger.Warning(format, args);
                    break;
                case LoggerEvent.Error:
                    ConsoleLogger.Error(format, args);
                    break;
            }
        }
    }
}
