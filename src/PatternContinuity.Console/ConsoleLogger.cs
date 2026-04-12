using Microsoft.Extensions.Logging;
using Persistence.DI;

namespace Persistence
{
    /// <summary>
    /// The console-specific logger implementation
    /// </summary>
    [Service(typeof(ILogger))]
    public class ConsoleLogger : BaseLogger
    {
        /// <summary>
        /// Log an info-level message, and (by default) send it to the participant
        /// </summary>
        public override void Info(string message, bool sendToParticipant = true)
        {
            Console.ForegroundColor = ConsoleColor.Gray;
            Console.WriteLine($"[INFO]: {message}");
            Console.ResetColor();

            if (sendToParticipant)
            {
                AddParticipantLogMessage(message, LogLevel.Information);
            }
        }

        /// <summary>
        /// Log an warning-level message, and (by default) send it to the participant
        /// </summary>
        public override void Warn(string message, bool sendToParticipant = true)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"[WARN]: {message}");
            Console.ResetColor();

            if (sendToParticipant)
            {
                AddParticipantLogMessage(message, LogLevel.Information);
            }
        }

        /// <summary>
        /// Log an error-level message, with an optional exception, and (by default) send it to the participant
        /// </summary>
        public override void Error(string message, Exception? ex = null, bool sendToParticipant = true)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[ERROR]: {message}");

            if (ex != null)
            {
                Console.WriteLine($"[ERROR]: Exception message: {ex.Message}");
                Console.WriteLine($"[ERROR]: Stack trace: {ex.StackTrace}");
            }

            Console.ResetColor();

            if (sendToParticipant)
            {
                AddParticipantLogMessage(message, LogLevel.Information, ex);
            }
        }
    }
}
