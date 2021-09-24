﻿using SMBeagle.FileDiscovery;
using SMBeagle.HostDiscovery;
using SMBeagle.NetworkDiscovery;
using SMBeagle.Output;
using SMBeagle.ShareDiscovery;
using CommandLine;
using CommandLine.Text;
using System;
using System.Collections.Generic;
using System.Linq;

namespace SMBeagle
{
    class Program
    {
        static void Main(string[] args)
        {
            Parser.Default.ParseArguments<Options>(args)
                .WithParsed(Run)
                .WithNotParsed(OutputHelp);
        }

        static void Run(Options opts)
        {
            if (opts.ElasticsearchHost == null && opts.CsvFile == null)
                OutputHelp(new Exception("Please provide either an elasticsearch host or a csv output filename"));

            if (!opts.Quiet)
                OutputHelper.ConsoleWriteLogo();

            if (opts.ElasticsearchHost != null)
                OutputHelper.EnableElasticsearchLogging($"http://{opts.ElasticsearchHost}:9200/");

            if (opts.CsvFile != null)
                OutputHelper.EnableCSVLogging(opts.CsvFile);

            NetworkFinder 
                nf = new();

            foreach (string network in opts.Networks)
                nf.AddNetwork(network, Enums.NetworkDiscoverySourceEnum.ARGS);

            // Discover networks automagically
            if (!opts.DisableNetworkDiscovery)
                nf.DiscoverNetworks();

            if (!opts.Quiet)
            {
                Console.WriteLine("Discovery finished, here is the result.  Some networks and addresses may yet be filtered");
                Console.WriteLine("I have built a list of the following {0} private networks:", nf.PrivateNetworks.Count);
                foreach (Network pn in nf.PrivateNetworks)
                    Console.WriteLine(pn);
                Console.WriteLine("I have built a list of the following {0} private addresses:", nf.PrivateAddresses.Count);
                foreach (string pa in nf.PrivateAddresses)
                    Console.WriteLine(pa);
            }

            if (opts.Verbose)
            {
                Console.WriteLine("I have discovered the following {0} addresses:", nf.Addresses.Count);
                foreach (string pa in nf.Addresses)
                    Console.WriteLine(pa);
            }
            
            // build list of discovered and provided hosts
            List<string> 
                addresses = opts.Hosts.ToList();

            addresses.AddRange(nf.PrivateAddresses);

            // build list of discovered and provided networks
            Int16 
                maxNetworkSizeForScanning = Int16.Parse(opts.MaxNetworkSizeForScanning);

            List<Network> networks = nf.PrivateNetworks
                .Where(item => item.IPVersion == 4) // We cannot scan ipv6 networks, they are HUGE, but we do scan the ipv6 hosts
                .Where(item => Int16.Parse(item.Cidr) >= maxNetworkSizeForScanning)
                .Where(item => !opts.ExcludedNetworks.Contains(item.ToString()))
                .ToList();

            // build list of provided exclusions
            List<string> filteredAddresses = new();

            if (opts.DisableLocalShares)
                filteredAddresses.AddRange(nf.LocalAddresses);

            filteredAddresses.AddRange(opts.ExcludedHosts.ToList());

            if (!opts.Quiet)
            {
                Console.WriteLine("Filtering is now complete and here is the result");
                Console.WriteLine("I shall scan the following {0} private networks:", networks.Count);
                foreach (Network pn in networks)
                    Console.WriteLine(pn);

                Console.WriteLine("I shall scan the following {0} private addresses:", addresses.Count);
                foreach (string pa in filteredAddresses)
                    Console.WriteLine(pa);
            }

            // Begin the scan for up hosts
            HostFinder 
                hf = new(addresses, networks, filteredAddresses);

            // Enumerate shares
            foreach (Host h in hf.ReachableHosts)
                ShareFinder.DiscoverDeviceShares(h);

            if (!opts.Quiet)
            {
                Console.WriteLine("Host scanning is now complete.  Reachabled hosts: ");
                foreach (Host host in hf.ReachableHosts)
                    Console.WriteLine(host);
            }

            Console.WriteLine("OF {0} reachable hosts, {1} had open shares", hf.ReachableHosts.Count, hf.ReachableHosts.Where(item => item.ShareCount > 0).ToList().Count);
            
            // Build list of uncPaths from up hosts
            List<string> uncPaths = new();
                foreach (Host h in hf.ReachableHosts.Where(item => item.ShareCount > 0))
                    uncPaths.AddRange(h.UNCPaths);

            // Find files on all the shares
            FileFinder 
                ff = new(paths: uncPaths, getPermissionsForSingleFileInDir: false, enumerateLocalDrives: opts.EnumerateLocalDrives, username: "", enumerateAcls: !opts.DontEnumerateAcls);

            OutputHelper.CloseAndFlush();

            // TODO: know when elasticsearch sink has finished outputting
        }

        static void OutputHelp(IEnumerable<Error> errs)
        {
            OutputHelper.ConsoleWriteLogo();
        }

        static void OutputHelp(Exception err)
        {
            OutputHelper.ConsoleWriteLogo();
            Console.WriteLine("!!!!------ ERROR ------!!!!");
            Console.WriteLine(err.Message);
            System.Environment.Exit(1);
        }

        #region Classes

        public class Options
        {
            [Option('c', "csv-file", Required = false, HelpText = "Output results to a CSV file by providing filepath")]
            public string CsvFile { get; set; }

            [Option('e', "elasticsearch-host", Required = false, HelpText = "Output results to elasticsearch by providing elasticsearch hostname (port is set to 9200 automatically)")]
            public string ElasticsearchHost { get; set; }

            [Option('l', "scan-local-drives", Required = false, HelpText = "Scan local drives on this machine")]
            public bool EnumerateLocalDrives { get; set; }
            [Option('L', "exclude-local-shares", Required = false, HelpText = "Do not scan local drives on this machine")]
            public bool DisableLocalShares { get; set; }

            [Option('D', "disable-network-discovery", Required = false, HelpText = "Disable network discovery")]
            public bool DisableNetworkDiscovery { get; set; }

            [Option('n', "network", Required = false, HelpText = "Manually add network to scan")]
            public IEnumerable<String> Networks { get; set; }
            [Option('N', "exclude-network", Required = false, HelpText = "Exclude a network from scanning")]
            public IEnumerable<string> ExcludedNetworks { get; set; }

            [Option('h', "host", Required = false, HelpText = "Manually add host to scan")]
            public IEnumerable<string> Hosts { get; set; }

            [Option('H', "exclude-host", Required = false, HelpText = "Exclude a host from scanning")]
            public IEnumerable<string> ExcludedHosts { get; set; }

            [Option('q', "quiet", Required = false, HelpText = "Disable unneccessary output")]
            public bool Quiet { get; set; }

            [Option('v', "verbose", Required = false, HelpText = "Give more output")]
            public bool Verbose { get; set; }

            [Option('m', "max-network-cidr-size", Required = false, Default = "20", HelpText = "Maximum network size to scan for SMB Hosts")]
            public string MaxNetworkSizeForScanning { get; set; }

            [Option('A', "dont-enumerate-acls", Required = false, Default = false, HelpText = "Skip enumeration of file ACLs")]
            public bool DontEnumerateAcls { get; set; }

            [Usage(ApplicationAlias = "SMBeagle")]
            public static IEnumerable<Example> Examples
            {
                get
                {
                    return new List<Example>() {
                        new Example("Output to a CSV file", new Options { CsvFile = "out.csv" })
                    };
                }
            }
        }

        #endregion
    }
}
