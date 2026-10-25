using Unity.Netcode.Components;

// Lets each player (owner) control their own position instead of the server.
public class ClientNetworkTransform : NetworkTransform
{
    protected override bool OnIsServerAuthoritative() => false;
}
