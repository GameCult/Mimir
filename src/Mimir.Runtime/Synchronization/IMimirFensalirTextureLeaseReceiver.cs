namespace Mimir.Runtime.Synchronization;

public interface IMimirFensalirTextureLeaseReceiver
{
    void AttachTextureLeaseClient(MimirFensalirTextureLeaseClient? client);
}
