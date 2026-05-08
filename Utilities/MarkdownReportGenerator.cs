using System;
using System.Text;
using System.Linq;
using NetBoxSync.Models;

namespace NetBoxSync.Utilities;

public static class MarkdownReportGenerator
{
    public static string Generate(VmAsset vm)
    {
        var sb = new StringBuilder();
        
        sb.AppendLine($"### Inventory Report ({DateTime.Now:yyyy-MM-dd})");
        sb.AppendLine();
        sb.AppendLine("| Property | Value |");
        sb.AppendLine("|---|---|");
        sb.AppendLine($"| OS | {(string.IsNullOrEmpty(vm.FullOsName) ? "Unknown" : vm.FullOsName)} |");
        sb.AppendLine($"| Provider/Host | {vm.Provider} / {vm.NodeName} |");
        sb.AppendLine($"| Discovery Method | {(string.IsNullOrEmpty(vm.DiscoveryMethod) ? "Unknown" : vm.DiscoveryMethod)} |");
        sb.AppendLine($"| Last Seen | {DateTime.Now:yyyy-MM-dd HH:mm:ss} |");
        
        if (!string.IsNullOrEmpty(vm.PrimaryVlan) || !string.IsNullOrEmpty(vm.VlanId))
        {
            string vlanVal = !string.IsNullOrEmpty(vm.PrimaryVlan) ? vm.PrimaryVlan : vm.VlanId;
            if (!string.IsNullOrEmpty(vm.VlanId) && !vlanVal.Contains(vm.VlanId)) 
                vlanVal += $" (ID: {vm.VlanId})";
            sb.AppendLine($"| VLAN | {vlanVal} |");
        }

        var filteredIps = vm.IpAddresses?
            .Where(ip => !string.IsNullOrEmpty(ip) && !ip.Contains(":") && ip != "127.0.0.1" && !ip.StartsWith("10.233."))
            .Distinct()
            .ToList();

        if (filteredIps != null && filteredIps.Any())
        {
            sb.AppendLine($"| IP Addresses | {string.Join("<br>", filteredIps)} |");
        }
        
        sb.AppendLine();
        
        if (vm.SoftwareList != null && vm.SoftwareList.Any())
        {
            sb.AppendLine("### Software Packages");
            sb.AppendLine();
            
            bool collapse = vm.SoftwareList.Count > 20;
            if (collapse)
            {
                sb.AppendLine("<details><summary>Click to expand</summary>");
                sb.AppendLine();
            }
            
            sb.AppendLine("| Package | Version |");
            sb.AppendLine("|---|---|");
            
            foreach (var sw in vm.SoftwareList)
            {
                var parts = sw.Split('|', 2);
                string pkg = parts[0].Trim();
                string ver = parts.Length > 1 ? parts[1].Trim() : "-";
                sb.AppendLine($"| {pkg} | {ver} |");
            }
            
            if (collapse)
            {
                sb.AppendLine();
                sb.AppendLine("</details>");
            }
        }
        
        return sb.ToString();
    }
}
