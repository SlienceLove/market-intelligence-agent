[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$TargetAddress,

    [int]$TargetPort = 80,

    [string]$ListenAddress = '127.0.0.1',

    [int]$ListenPort = 18080
)

$ErrorActionPreference = 'Stop'

$source = @'
using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

public static class DifyLocalTcpProxy
{
    public static void Run(string listenAddress, int listenPort, string targetAddress, int targetPort)
    {
        var listener = new TcpListener(IPAddress.Parse(listenAddress), listenPort);
        listener.Start(64);

        try
        {
            while (true)
            {
                var client = listener.AcceptTcpClient();
                ThreadPool.QueueUserWorkItem(_ => Forward(client, targetAddress, targetPort));
            }
        }
        finally
        {
            listener.Stop();
        }
    }

    private static void Forward(TcpClient client, string targetAddress, int targetPort)
    {
        using (client)
        using (var server = new TcpClient())
        {
            try
            {
                server.Connect(targetAddress, targetPort);
            }
            catch
            {
                return;
            }

            using (var clientStream = client.GetStream())
            using (var serverStream = server.GetStream())
            {
                var clientToServer = Pump(clientStream, serverStream);
                var serverToClient = Pump(serverStream, clientStream);

                try
                {
                    Task.WaitAny(clientToServer, serverToClient);
                }
                catch
                {
                }
            }
        }
    }

    private static async Task Pump(NetworkStream source, NetworkStream destination)
    {
        var buffer = new byte[64 * 1024];

        try
        {
            int count;
            while ((count = await source.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
            {
                await destination.WriteAsync(buffer, 0, count).ConfigureAwait(false);
                await destination.FlushAsync().ConfigureAwait(false);
            }
        }
        catch
        {
        }
    }
}
'@

Add-Type -TypeDefinition $source -Language CSharp
[DifyLocalTcpProxy]::Run($ListenAddress, $ListenPort, $TargetAddress, $TargetPort)
