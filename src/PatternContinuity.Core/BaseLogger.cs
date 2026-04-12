using Microsoft.Extensions.Logging;

namespace Persistence
{
    /// <summary>
    /// Base logger with common methods for display-specific loggers to use
    /// </summary>
    public abstract class BaseLogger : ILogger
    {
        /// <summary>
        /// Add a message to be sent to the participant in the next log batch
        /// </summary>
        public void AddParticipantLogMessage(string message, LogLevel level, Exception? ex = null)
        {
            // TODO: Actually add log messages to a block to get sent with the next input, or on a timer
        }

        /// <summary>
        /// Log an info-level message, and (by default) send it to the participant
        /// </summary>
        public abstract void Info(string message, bool sendToParticipant = true);

        /// <summary>
        /// Log an warning-level message, and (by default) send it to the participant
        /// </summary>
        public abstract void Warn(string message, bool sendToParticipant = true);

        /// <summary>
        /// Log an error-level message, with an optional exception, and (by default) send it to the participant
        /// </summary>
        public abstract void Error(string message, Exception? ex = null, bool sendToParticipant = true);
    }
}
