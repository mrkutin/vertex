namespace Vertex.Core.Protocol;

/// <summary>
/// MQTT topic builders matching the Go implementation byte-for-byte.
/// Topic structure (mirror of Swift <c>Topics</c> and Kotlin <c>Topics</c>):
/// <list type="bullet">
///   <item>Data:      <c>vpn/{exit}/{name}/out</c> (client→exit), <c>vpn/{exit}/{name}/in</c> (exit→client).</item>
///   <item>Control:   <c>vpn/{exit}/control/join</c> (join request), <c>vpn/{exit}/{name}/control</c> (assign response).</item>
///   <item>Discovery: <c>discovery/exits/{exit}</c> (exit heartbeats, retained).</item>
/// </list>
/// </summary>
public static class Topics
{
    /// <summary>Client publishes IP packets upstream. <c>vpn/{exit}/{name}/out</c></summary>
    public static string Upload(string exit, string name) => $"vpn/{exit}/{name}/out";

    /// <summary>Client subscribes to receive IP packets downstream. <c>vpn/{exit}/{name}/in</c></summary>
    public static string Download(string exit, string name) => $"vpn/{exit}/{name}/in";

    /// <summary>Client subscribes to download from any exit (wildcard). <c>vpn/+/{name}/in</c></summary>
    public static string DownloadAny(string name) => $"vpn/+/{name}/in";

    /// <summary>Client publishes join handshake. <c>vpn/{exit}/control/join</c></summary>
    public static string Join(string exit) => $"vpn/{exit}/control/join";

    /// <summary>Client subscribes for control responses (assign, etc). <c>vpn/{exit}/{name}/control</c></summary>
    public static string Control(string exit, string name) => $"vpn/{exit}/{name}/control";

    /// <summary>Client subscribes for control from any exit (wildcard). <c>vpn/+/{name}/control</c></summary>
    public static string ControlAny(string name) => $"vpn/+/{name}/control";

    /// <summary>Exit publishes discovery heartbeats (retained). <c>discovery/exits/{exit}</c></summary>
    public static string Discovery(string exit) => $"discovery/exits/{exit}";

    /// <summary>Subscribe to all exit discovery heartbeats. <c>discovery/exits/+</c></summary>
    public const string DiscoveryAll = "discovery/exits/+";

    /// <summary>
    /// MQTT topic-filter matching: <c>+</c> matches one level, <c>#</c>
    /// matches the rest of the topic (only valid as the final part).
    /// Mirror of Swift <c>topicMatches</c> and Kotlin <c>Topics.matches</c>.
    /// </summary>
    public static bool Matches(string topic, string pattern)
    {
        var topicParts   = topic.Split('/');
        var patternParts = pattern.Split('/');

        int ti = 0, pi = 0;
        while (pi < patternParts.Length)
        {
            string pp = patternParts[pi];
            if (pp == "#") return true;
            if (ti >= topicParts.Length) return false;
            if (pp != "+" && pp != topicParts[ti]) return false;
            ti++;
            pi++;
        }
        return ti == topicParts.Length;
    }
}
