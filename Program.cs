using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Opc.Ua;
using Opc.Ua.Client;
using Opc.Ua.Configuration;

class Program
{
    private static ISession session;

    // Configuration constants
    private const int OperationTimeoutMs = 15000;
    private const int SessionTimeoutMs = 60000;
    private const int MaxErrorMessageLength = 80;
    private const uint StandardUaServerTimeNodeId = 2258;
    private const int PortScanTimeoutMs = 3000;

    // Common OPC UA ports
    private static readonly int[] CommonOpcUaPorts = new int[]
    {
        4840,
        4843,
        49320,
        49321,
        8080,
        8081,
        8443,
        50000,
        50001,
    };

    // Port range for scanning
    private const int PortRangeStart = 4800;
    private const int PortRangeEnd = 5000;

    // Default OPC UA server URLs to probe
    private static readonly string[] DefaultServerUrls = new string[]
    {
        "opc.tcp://localhost:49320",
        "opc.tcp://localhost:4840",
        "opc.tcp://localhost:4843",
    };

    static async Task Main(string[] args)
    {
        Console.WriteLine("=== OPC UA Server Connector ===\n");

        try
        {
            ApplicationConfiguration config = await BuildClientConfig();
            List<EndpointDescription> endpoints = new List<EndpointDescription>();

            Console.WriteLine("Step 1: Scanning default OPC UA ports...");
            endpoints = await DiscoverEndpointsAsync(config, DefaultServerUrls);

            if (endpoints.Count > 0)
            {
                Console.WriteLine(string.Format("Found {0} server(s) on default ports.\n", endpoints.Count));
            }
            else
            {
                Console.WriteLine("No servers found on default ports.\n");
            }

            string additionalChoice = PromptForAdditionalDiscovery();
            if (!string.IsNullOrEmpty(additionalChoice))
            {
                List<EndpointDescription> additionalEndpoints = await PerformAdditionalDiscovery(config, additionalChoice);
                endpoints.AddRange(additionalEndpoints);
            }

            if (endpoints.Count == 0)
            {
                Console.WriteLine("No OPC UA servers found. Exiting.");
                return;
            }

            EndpointDescription chosen = PromptUserSelection(endpoints);

            if (chosen == null)
            {
                Console.WriteLine("No server selected. Exiting.");
                return;
            }

            await ConnectAndVerify(config, chosen);
        }
        finally
        {
            if (session != null)
                session.Dispose();
        }

        Console.WriteLine("\nPress any key to exit...");
        Console.ReadKey();
    }

    static string PromptForAdditionalDiscovery()
    {
        Console.WriteLine("Step 2: Would you like to search for servers on other ports/hosts?");
        Console.WriteLine("  [1] Scan common OPC UA ports on localhost");
        Console.WriteLine("  [2] Scan a custom port range on localhost");
        Console.WriteLine("  [3] Enter custom endpoint URL(s)");
        Console.WriteLine("  [4] Scan ports on a specific host");
        Console.WriteLine("  [0] Skip additional discovery");
        Console.Write("\nEnter your choice (0-4): ");

        string choice = Console.ReadLine();
        return choice != null ? choice.Trim() : string.Empty;
    }

    static async Task<List<EndpointDescription>> PerformAdditionalDiscovery(ApplicationConfiguration config, string choice)
    {
        switch (choice)
        {
            case "1":
                return await DiscoverOnCommonPorts(config, "localhost");
            case "2":
                return await DiscoverOnCustomPortRange(config, "localhost");
            case "3":
                return await DiscoverOnCustomUrls(config);
            case "4":
                return await DiscoverOnCustomHost(config);
            default:
                return new List<EndpointDescription>();
        }
    }

    static async Task<ApplicationConfiguration> BuildClientConfig()
    {
        var config = new ApplicationConfiguration()
        {
            ApplicationName = "OpcConnectorTest",
            ApplicationType = ApplicationType.Client,
            SecurityConfiguration = new SecurityConfiguration
            {
                AutoAcceptUntrustedCertificates = true,
                ApplicationCertificate = new CertificateIdentifier()
            },
            TransportQuotas = new TransportQuotas { OperationTimeout = OperationTimeoutMs },
            ClientConfiguration = new ClientConfiguration { DefaultSessionTimeout = SessionTimeoutMs }
        };

        await config.Validate(ApplicationType.Client);

        config.CertificateValidator.CertificateValidation += (sender, eventArgs) => { eventArgs.Accept = true; };

        return config;
    }

    static async Task<List<EndpointDescription>> DiscoverEndpointsAsync(ApplicationConfiguration config, string[] urlsToProbe)
    {
        var found = new List<EndpointDescription>();

        Console.WriteLine(string.Format("\nScanning {0} endpoint(s)...", urlsToProbe.Length));

        foreach (string url in urlsToProbe)
        {
            try
            {
                Console.Write(string.Format("  Probing {0} ... ", url));

                var description = new EndpointDescription
                {
                    EndpointUrl = url,
                    SecurityMode = MessageSecurityMode.None,
                    SecurityPolicyUri = SecurityPolicies.None,
                    Server = new ApplicationDescription()
                };

                var endpoint = new ConfiguredEndpoint(null, description);

                ISession testSession = await Session.Create(
                    config,
                    endpoint,
                    false,
                    false,
                    "DiscoveryProbe",
                    (uint)PortScanTimeoutMs,
                    new UserIdentity(),
                    null
                );

                if (testSession != null)
                {
                    Console.WriteLine("FOUND");
                    found.Add(description);
                    testSession.Close();
                }
                else
                {
                    Console.WriteLine("no response");
                }
            }
            catch (Exception ex)
            {
                string reason = TruncateMessage(ex.Message, MaxErrorMessageLength);
                Console.WriteLine(string.Format("no response ({0})", reason));
            }
        }

        Console.WriteLine();
        return found;
    }

    static async Task<List<EndpointDescription>> DiscoverOnCommonPorts(ApplicationConfiguration config, string host)
    {
        Console.WriteLine(string.Format("\nScanning common OPC UA ports on {0}...", host));
        string[] urls = CommonOpcUaPorts.Select(port => string.Format("opc.tcp://{0}:{1}", host, port)).ToArray();
        return await DiscoverEndpointsAsync(config, urls);
    }

    static async Task<List<EndpointDescription>> DiscoverOnCustomPortRange(ApplicationConfiguration config, string host)
    {
        Console.Write(string.Format("\nEnter port range start (default {0}): ", PortRangeStart));
        string startInput = Console.ReadLine();
        int start;
        if (!int.TryParse(startInput != null ? startInput.Trim() : string.Empty, out start))
            start = PortRangeStart;

        Console.Write(string.Format("Enter port range end (default {0}): ", PortRangeEnd));
        string endInput = Console.ReadLine();
        int end;
        if (!int.TryParse(endInput != null ? endInput.Trim() : string.Empty, out end))
            end = PortRangeEnd;

        if (start > end)
        {
            int tmp = start;
            start = end;
            end = tmp;
        }

        Console.WriteLine(string.Format("Scanning ports {0}-{1} on {2}. This may take a while...", start, end, host));

        string[] urls = Enumerable.Range(start, end - start + 1)
            .Select(port => string.Format("opc.tcp://{0}:{1}", host, port))
            .ToArray();

        return await DiscoverEndpointsAsync(config, urls);
    }

    static async Task<List<EndpointDescription>> DiscoverOnCustomUrls(ApplicationConfiguration config)
    {
        var urls = new List<string>();

        Console.WriteLine("\nEnter custom endpoint URLs (one per line, empty line to finish):");
        while (true)
        {
            Console.Write("> ");
            string input = Console.ReadLine();
            string url = input != null ? input.Trim() : string.Empty;

            if (string.IsNullOrEmpty(url))
                break;

            if (!url.StartsWith("opc.tcp://") && !url.StartsWith("opc.https://"))
                url = "opc.tcp://" + url;

            urls.Add(url);
        }

        if (urls.Count == 0)
        {
            Console.WriteLine("No URLs entered.");
            return new List<EndpointDescription>();
        }

        return await DiscoverEndpointsAsync(config, urls.ToArray());
    }

    static async Task<List<EndpointDescription>> DiscoverOnCustomHost(ApplicationConfiguration config)
    {
        Console.Write("Enter hostname or IP address: ");
        string input = Console.ReadLine();
        string host = input != null ? input.Trim() : string.Empty;

        if (string.IsNullOrEmpty(host))
        {
            Console.WriteLine("No host entered.");
            return new List<EndpointDescription>();
        }

        Console.WriteLine("Which ports would you like to scan?");
        Console.WriteLine("  [1] Common OPC UA ports");
        Console.WriteLine("  [2] Custom port range");
        Console.Write("Enter choice (1 or 2): ");

        string choiceInput = Console.ReadLine();
        string choice = choiceInput != null ? choiceInput.Trim() : string.Empty;

        if (choice == "1")
        {
            Console.WriteLine(string.Format("\nScanning common ports on {0}...", host));
            string[] urls = CommonOpcUaPorts.Select(port => string.Format("opc.tcp://{0}:{1}", host, port)).ToArray();
            return await DiscoverEndpointsAsync(config, urls);
        }
        else if (choice == "2")
        {
            Console.Write(string.Format("Enter port range start (default {0}): ", PortRangeStart));
            string startInput = Console.ReadLine();
            int start;
            if (!int.TryParse(startInput != null ? startInput.Trim() : string.Empty, out start))
                start = PortRangeStart;

            Console.Write(string.Format("Enter port range end (default {0}): ", PortRangeEnd));
            string endInput = Console.ReadLine();
            int end;
            if (!int.TryParse(endInput != null ? endInput.Trim() : string.Empty, out end))
                end = PortRangeEnd;

            if (start > end)
            {
                int tmp = start;
                start = end;
                end = tmp;
            }

            Console.WriteLine(string.Format("Scanning ports {0}-{1} on {2}. This may take a while...", start, end, host));

            string[] urls = Enumerable.Range(start, end - start + 1)
                .Select(port => string.Format("opc.tcp://{0}:{1}", host, port))
                .ToArray();

            return await DiscoverEndpointsAsync(config, urls);
        }

        return new List<EndpointDescription>();
    }

    private static string TruncateMessage(string message, int maxLength)
    {
        if (message.Length > maxLength)
            return message.Substring(0, maxLength) + "...";
        return message;
    }

    static EndpointDescription PromptUserSelection(List<EndpointDescription> endpoints)
    {
        Console.WriteLine("Discovered endpoints:");
        Console.WriteLine(new string('-', 60));

        for (int i = 0; i < endpoints.Count; i++)
        {
            EndpointDescription ep = endpoints[i];
            string secPolicy = ExtractSecurityPolicy(ep.SecurityPolicyUri);

            string appName = (ep.Server != null && ep.Server.ApplicationName != null)
                ? ep.Server.ApplicationName.Text
                : "Unknown";

            Console.WriteLine(string.Format("  [{0}] URL      : {1}", i + 1, ep.EndpointUrl));
            Console.WriteLine(string.Format("       Server   : {0}", appName));
            Console.WriteLine(string.Format("       Security : {0}", secPolicy));
            Console.WriteLine(string.Format("       Mode     : {0}", ep.SecurityMode));
            Console.WriteLine();
        }

        Console.Write("Enter the number of the server you want to connect to (or 0 to cancel): ");

        string line = Console.ReadLine();
        int choice;
        if (int.TryParse(line != null ? line.Trim() : string.Empty, out choice) &&
            choice >= 1 && choice <= endpoints.Count)
        {
            EndpointDescription selected = endpoints[choice - 1];
            Console.WriteLine(string.Format("\nSelected: {0}\n", selected.EndpointUrl));
            return selected;
        }

        return null;
    }

    private static string ExtractSecurityPolicy(string securityPolicyUri)
    {
        if (string.IsNullOrEmpty(securityPolicyUri))
            return "Unknown";

        int hashIndex = securityPolicyUri.IndexOf('#');
        if (hashIndex >= 0)
            return securityPolicyUri.Substring(hashIndex + 1);

        return securityPolicyUri;
    }

    static async Task ConnectAndVerify(ApplicationConfiguration config, EndpointDescription endpointDesc)
    {
        Console.WriteLine("Attempting connection...");

        try
        {
            var endpoint = new ConfiguredEndpoint(
                null,
                endpointDesc,
                EndpointConfiguration.Create(config)
            );

            session = await Session.Create(
                config,
                endpoint,
                false,
                false,
                "ConnectorTestSession",
                (uint)SessionTimeoutMs,
                new UserIdentity(),
                null
            );

            if (!session.Connected)
            {
                Console.WriteLine("Session created but reports as NOT connected.");
                return;
            }

            Console.WriteLine("Session established.\n");

            NodeId currentTimeNode = new NodeId(StandardUaServerTimeNodeId, 0);
            DataValue serverTime = session.ReadValue(currentTimeNode);

            if (StatusCode.IsGood(serverTime.StatusCode))
            {
                Console.WriteLine("=== CONNECTION VERIFIED ===");
                Console.WriteLine(string.Format("  Endpoint   : {0}", session.Endpoint.EndpointUrl));
                Console.WriteLine(string.Format("  Server time: {0}", serverTime.Value));
                Console.WriteLine(string.Format("  Status     : {0}", serverTime.StatusCode));
                Console.WriteLine(string.Format("  Session ID : {0}", session.SessionId));
            }
            else
            {
                Console.WriteLine(string.Format("Connected but live-read returned bad status: {0}", serverTime.StatusCode));
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine(string.Format("Connection failed: {0}", ex.Message));
        }
    }
}
