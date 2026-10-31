namespace Smart_Tools.WPF.Models
{
    /// <summary>
    /// One TCP or UDP endpoint and the process that owns it (a row of Windows' connection table). For UDP the remote
    /// side is "*" and the state is empty. Shared by Process Killer (find a port) and Connections.
    /// </summary>
    public sealed record EndpointUse(string Protocol, string LocalAddress, int LocalPort, string RemoteAddress, int RemotePort, string State, int Pid);
}
