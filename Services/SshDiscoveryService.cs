using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Threading.Tasks;
using Renci.SshNet;

namespace NetBoxSync.Services;

public class SshDiscoveryService
{
    public async Task<List<string>> GetInstalledSoftwareAsync(string ipAddress, string username, string password)
    {
        var softwareList = new List<string>();

        try
        {
            // 1. Perform a quick TCP port check on port 22 (timeout 2s)
            if (!await IsPortOpenAsync(ipAddress, 22, TimeSpan.FromSeconds(2)))
            {
                return softwareList; // Port closed or timeout
            }

            // 2. Connect via SSH
            var connectionInfo = new ConnectionInfo(ipAddress, username, new PasswordAuthenticationMethod(username, password))
            {
                Timeout = TimeSpan.FromSeconds(5)
            };

            using var client = new SshClient(connectionInfo);
            client.Connect();

            // 3. Detect OS family
            var osDetectCmd = client.CreateCommand("uname -s");
            osDetectCmd.Execute();
            string osName = osDetectCmd.Result.Trim().ToLowerInvariant();

            if (osDetectCmd.ExitStatus != 0 || string.IsNullOrEmpty(osName) || osName.Contains("not recognized"))
            {
                // Likely Windows Server with OpenSSH
                // Adapted powershell query to cleanly format as 'Name|Version'
                var psCmd = client.CreateCommand("powershell \"Get-ItemProperty HKLM:\\Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\* | Where-Object { $_.DisplayName } | ForEach-Object { \\\"$($_.DisplayName)|$($_.DisplayVersion)\\\" }\"");
                psCmd.Execute();
                
                var lines = psCmd.Result.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                softwareList.AddRange(lines.Select(l => l.Trim()).Where(l => l.Contains('|')));
            }
            else if (osName == "linux")
            {
                var releaseCmd = client.CreateCommand("cat /etc/os-release");
                releaseCmd.Execute();
                string osReleaseContent = releaseCmd.Result.ToLowerInvariant();

                if (osReleaseContent.Contains("ubuntu") || osReleaseContent.Contains("debian"))
                {
                    // Debian/Ubuntu
                    var dpkgCmd = client.CreateCommand("dpkg-query -W -f='${Package}|${Version}\n'");
                    dpkgCmd.Execute();
                    
                    var lines = dpkgCmd.Result.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    softwareList.AddRange(lines.Select(l => l.Trim()));
                }
                else
                {
                    // Fallback to RHEL/CentOS/Fedora assumption
                    var rpmCmd = client.CreateCommand("rpm -qa --qf '%{NAME}|%{VERSION}\n'");
                    rpmCmd.Execute();
                    
                    var lines = rpmCmd.Result.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    softwareList.AddRange(lines.Select(l => l.Trim()));
                }
            }
        }
        catch (Exception ex)
        {
            NetBoxSync.Utilities.SyncLogger.Info($"[SSH] Discovery Failed for {ipAddress}: {ex.Message}");
        }

        return softwareList.Distinct().ToList();
    }

    private async Task<bool> IsPortOpenAsync(string ipAddress, int port, TimeSpan timeout)
    {
        try
        {
            using var tcpClient = new TcpClient();
            var connectTask = tcpClient.ConnectAsync(ipAddress, port);
            
            // Wait for either the connection to succeed or the timeout to be reached
            if (await Task.WhenAny(connectTask, Task.Delay(timeout)) == connectTask)
            {
                return tcpClient.Connected;
            }
            
            return false;
        }
        catch
        {
            return false;
        }
    }
}
