using System.Globalization;
using System.ServiceProcess;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;

namespace Vertex.Service.Net;

/// <summary>
/// Plugs Windows' Smart Multi-Homed Name Resolution leak: by default the
/// DNS Client service queries the resolvers on every interface in
/// parallel, including the physical NIC, so a TUN-only DNS won't actually
/// keep DNS inside the tunnel. Mitigation:
/// <list type="number">
///   <item>NRPT (Name Resolution Policy Table) entry for "<c>.</c>" that
///   pins all queries to the TUN's DNS server. Reference:
///   <c>HKLM\SYSTEM\CurrentControlSet\Services\Dnscache\Parameters\DnsPolicyConfig</c>
///   per <c>https://learn.microsoft.com/windows-server/networking/technologies/nrpt</c>.</item>
///   <item>Disable LLMNR + NetBIOS multicast so name resolution can't
///   race the NRPT entry over the local link
///   (<c>HKLM\SOFTWARE\Policies\Microsoft\Windows NT\DNSClient\EnableMulticast = 0</c>).</item>
/// </list>
/// All changes are reverted by <see cref="Cleanup"/> on tunnel teardown
/// or service stop. Cleanup is idempotent.
///
/// Phase 1.8 ships the registry portion. The optional newer
/// <c>SetInterfaceDnsSettings</c> iphlpapi API
/// (Windows 10 2004+) for per-interface DNS override is left as a TODO
/// for Phase 1.11 once the basic NRPT path is verified on the VM.
/// </summary>
public sealed class DnsLeakGuard
{
    private const string NrptKeyRoot = @"SYSTEM\CurrentControlSet\Services\Dnscache\Parameters\DnsPolicyConfig";
    private const string DnsClientPolicyKey = @"SOFTWARE\Policies\Microsoft\Windows NT\DNSClient";
    private const string VertexNrptName = "Vertex-AllDomains";

    private readonly ILogger _log;
    private bool _llmnrPolicyApplied;
    private bool _nrptApplied;
    private string? _previousLlmnrValue;

    public DnsLeakGuard(ILogger? log = null) => _log = log ?? NullLogger.Instance;

    /// <summary>
    /// Enable NRPT so all DNS queries route to <paramref name="tunDnsServers"/>
    /// (TUN-side resolver, e.g. 10.9.0.1), and disable LLMNR / NetBIOS
    /// multicast so name resolution can't side-step the NRPT entry.
    /// </summary>
    public void Apply(IReadOnlyList<string> tunDnsServers)
    {
        if (tunDnsServers.Count == 0)
        {
            _log.LogWarning("Apply called with empty DNS server list — skipping");
            return;
        }

        try
        {
            using var policyKey = Registry.LocalMachine.CreateSubKey($"{NrptKeyRoot}\\{VertexNrptName}");
            // Name space "." (root) so EVERY name passes through the rule.
            // REG_MULTI_SZ stored with terminator pair to satisfy NRPT parser.
            policyKey.SetValue("Name",       new[] { "." },                                RegistryValueKind.MultiString);
            policyKey.SetValue("Comment",    "Vertex VPN: pin DNS to tunnel resolver",     RegistryValueKind.String);
            policyKey.SetValue("Version",    2,                                            RegistryValueKind.DWord);
            policyKey.SetValue("ConfigOptions", 0x8 /* DNS_POLICY_CONFIG_GENERIC_DNS_SERVERS */,
                                                                                            RegistryValueKind.DWord);
            policyKey.SetValue("GenericDNSServers", string.Join(';', tunDnsServers),       RegistryValueKind.String);
            _nrptApplied = true;
            _log.LogInformation("NRPT entry installed → DNS pinned to [{Servers}]", string.Join(", ", tunDnsServers));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "NRPT registry write failed (admin required?)");
        }

        // The Dnscache service caches its NRPT in memory; without a
        // PARAMCHANGE notification the new rule sits in the registry
        // but doesn't take effect until the next service restart /
        // reboot, leaving DNS leaking through the physical NIC. Mirror
        // of WireGuard-Windows behaviour. Phase 1.8 review MAJOR-6.
        NotifyDnscache();

        try
        {
            using var key = Registry.LocalMachine.CreateSubKey(DnsClientPolicyKey);
            object? prev = key.GetValue("EnableMulticast");
            _previousLlmnrValue = prev?.ToString();
            key.SetValue("EnableMulticast", 0, RegistryValueKind.DWord);
            _llmnrPolicyApplied = true;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Disabling LLMNR failed");
        }
    }

    /// <summary>Roll back every change made by <see cref="Apply"/>. Idempotent.</summary>
    public void Cleanup()
    {
        if (_nrptApplied)
        {
            try
            {
                Registry.LocalMachine.DeleteSubKeyTree($"{NrptKeyRoot}\\{VertexNrptName}", throwOnMissingSubKey: false);
                _log.LogInformation("NRPT entry removed");
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Removing NRPT registry key failed");
            }
            _nrptApplied = false;
        }

        if (_llmnrPolicyApplied)
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(DnsClientPolicyKey, writable: true);
                if (key is not null)
                {
                    if (_previousLlmnrValue is null) key.DeleteValue("EnableMulticast", throwOnMissingValue: false);
                    else                              key.SetValue("EnableMulticast",
                                                                     int.Parse(_previousLlmnrValue, CultureInfo.InvariantCulture),
                                                                     RegistryValueKind.DWord);
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Restoring LLMNR setting failed");
            }
            _llmnrPolicyApplied = false;
        }

        NotifyDnscache();
    }

    private void NotifyDnscache()
    {
        try
        {
            using var sc = new ServiceController("Dnscache");
            // SERVICE_CONTROL_PARAMCHANGE = 6 — re-read configuration
            // without restart. Dnscache documents this as the supported
            // "reload NRPT" signal.
            sc.ExecuteCommand(6);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to notify Dnscache of NRPT change — DNS leak possible until reboot");
        }
    }
}
