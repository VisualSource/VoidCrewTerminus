using System;
using System.Collections.Generic;
using System.Linq;
using VoidCrewTerminus.Net;

namespace VoidCrewTerminus.Tests;

// The third adapter at the transport seam: records instead of transmitting.
//
// This is what makes the gate rules testable at all. Set the two facts the
// transport reports, drive ForgeNetSync's real entry points, and assert on what
// would have gone on the wire.
internal sealed class RecordingTransport : IForgeTransport
{
    internal enum Target { Others, Master, Peer }

    internal readonly record struct Sent(Target Target, Type Message, object[] Payload, int ActorNumber);

    private readonly List<Sent> _sent = new();

    internal IReadOnlyList<Sent> All => _sent;
    internal int Count => _sent.Count;

    public bool IsAuthority { get; set; }
    public bool HasPeers { get; set; }

    // The two states worth naming, since almost every gate assertion is one of them.
    internal static RecordingTransport Host() => new() { IsAuthority = true, HasPeers = true };
    internal static RecordingTransport Client() => new() { IsAuthority = false, HasPeers = true };

    // Solo: authority, but nobody to talk to. Matches OfflineTransport's answers.
    internal static RecordingTransport Solo() => new() { IsAuthority = true, HasPeers = false };

    public void SendToOthers(Type message, object[] payload) =>
        _sent.Add(new Sent(Target.Others, message, payload, 0));

    public void SendToMaster(Type message, object[] payload) =>
        _sent.Add(new Sent(Target.Master, message, payload, 0));

    public void SendToPeer(int actorNumber, Type message, object[] payload) =>
        _sent.Add(new Sent(Target.Peer, message, payload, actorNumber));

    internal IEnumerable<Sent> OfType<TMessage>() =>
        _sent.Where(s => s.Message == typeof(TMessage));

    internal Sent Only()
    {
        if (_sent.Count != 1)
            throw new InvalidOperationException($"expected exactly 1 send, got {_sent.Count}");
        return _sent[0];
    }
}
