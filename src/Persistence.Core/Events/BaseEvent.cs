namespace Persistence.Events
{
    public class BaseEvent : EventArgs
    {
        public CancellationToken? cancellationToken;
    }
}
