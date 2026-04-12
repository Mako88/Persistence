namespace Persistence
{
    /// <summary>
    /// A class for logging messages
    /// </summary>
    public interface ILogger
    {
        /// <summary>
        /// Log an info-level message, and (by default) send it to the participant
        /// </summary>
        void Info(string message, bool sendToParticipant = true);

        /// <summary>
        /// Log an warning-level message, and (by default) send it to the participant
        /// </summary>
        void Warn(string message, bool sendToParticipant = true);

        /// <summary>
        /// Log an error-level message, with an optional exception, and (by default) send it to the participant
        /// </summary>
        void Error(string message, Exception? ex = null, bool sendToParticipant = true);
    }
}
