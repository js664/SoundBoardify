using VRSoundboard;
using Xunit;

namespace SimplySound.Tests;

public sealed class WebSocketHubTests
{
    [Fact]
    public async Task BroadcastWithoutConnectedClientsDoesNotSerializeThePayload()
    {
        var hub = new WebSocketHub();

        await hub.Broadcast("position", new PayloadThatMustNotBeSerialized());
    }

    private sealed class PayloadThatMustNotBeSerialized
    {
        public string Value => throw new InvalidOperationException("Payload serialization was unnecessary.");
    }
}
