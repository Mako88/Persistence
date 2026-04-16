using Persistence.DI;
using Persistence.Runtime;
using System.Text;

namespace Persistence.Services
{
    /// <summary>
    /// Class for testing infrastructure without a participant
    /// </summary>
    [Service(typeof(IModelClient))]
    public class LocalModelClient : IModelClient
    {
        private readonly IDisplayProvider display;

        public LocalModelClient(IDisplayProvider display)
        {
            this.display = display;
        }

        public async Task<string> CompleteAsync(List<ChatMessage> messages, CancellationToken ct = default)
        {
            var sb = new StringBuilder();

            var index = 1;

            foreach (var message in messages)
            {
                sb.AppendLine($"Message {index}");
                sb.AppendLine($"Role: {message.Role}");
                sb.AppendLine(message.Content);
                sb.AppendLine("\n------------------\n");
                index++;
            }

            display.ShowDebugInfo(sb.ToString());

            return await display.RequestInputAsync(ct) ?? "{}";
        }
    }
}
