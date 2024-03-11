using Azure.Core;
using Azure.Identity;
using Azure.ResourceManager;
using Azure.ResourceManager.Dns;
using Azure.ResourceManager.Dns.Models;
using System.Net;
using System.Text.RegularExpressions;

namespace Azure_Hosted_Dynamic_DNS
{
    public class Worker : BackgroundService
    {
        private string lastIP = string.Empty;
        private ArmClient? armclient = null;
        private readonly ILogger<Worker> _logger;

        readonly string? azureTenant = Environment.GetEnvironmentVariable("AzureTenantID");
        readonly string? azureClientID = Environment.GetEnvironmentVariable("AzureClientID");
        readonly string? azureClientSecret = Environment.GetEnvironmentVariable("AzureClientSecret");
        readonly string? azureSub = Environment.GetEnvironmentVariable("AzureSubID");
        readonly string? azureResourceName = Environment.GetEnvironmentVariable("AzureResourceName");
        readonly string? dnsZoneName = Environment.GetEnvironmentVariable("DNSZoneName");
        readonly string? urlForExternalIP = Environment.GetEnvironmentVariable("IPServiceURL");
        readonly string? regexToParseURLResponse = Environment.GetEnvironmentVariable("URLParsingRegex");

        private static readonly string? baseDNSARecord = Environment.GetEnvironmentVariable("DNSARecordToUpdate");
        readonly string dnsARecordToUpdate = baseDNSARecord ?? "HA";

        readonly int pollFrequency = int.TryParse(Environment.GetEnvironmentVariable("PollFrequency"), out int parsedPollFreq) ? parsedPollFreq : 300000;
        readonly int dnsARecordTTL = int.TryParse(Environment.GetEnvironmentVariable("DNSARecordTTL"), out int parsedTTL) ? parsedTTL : 300;


        public Worker(ILogger<Worker> logger)
        {
            _logger = logger;
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation($"Worker has Started.");
            }
        }


        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            ClientSecretCredential? cred = null;

            if (IsConfigInValid())
            {
                _logger.LogError($"Missing Config, Please Ensure supplied config is correct and present.");
                return;
            }

            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation("Configuration Used: ");
                _logger.LogInformation("DNSZoneName: {dnsZoneName}", dnsZoneName);
                _logger.LogInformation("DNSARecordToUpdate: {dnsARecordToUpdate}", dnsARecordToUpdate);
                _logger.LogInformation("Poll Frequency: {pollFrequency}", pollFrequency);
                _logger.LogInformation("DNSARecordTTL: {dnsARecordTTL} ", dnsARecordTTL);
                _logger.LogInformation("URL For Getting External IP: {urlForExternalIP}", urlForExternalIP);
                _logger.LogInformation("Regex for IP Parsing: {regexToParseURLResponse}", regexToParseURLResponse);
            }
            if (armclient == null)
            {
                cred = new ClientSecretCredential(azureTenant, azureClientID, azureClientSecret);
                armclient = new ArmClient(cred);
            }
            using var client = new HttpClient();
            while (!stoppingToken.IsCancellationRequested)
            {
                string? httpStringContent = null;

                int loopCount = 0;
                while (loopCount < 3)
                {
                    var result = await client.GetAsync(urlForExternalIP, stoppingToken);
                    if (result.IsSuccessStatusCode)
                    {
                        httpStringContent = await result.Content.ReadAsStringAsync(stoppingToken);
                        break;
                    }
                    loopCount++;
                }
                if (string.IsNullOrWhiteSpace(httpStringContent))
                {
                    _logger.LogInformation("Could not get any information from {urlForExternalIP}. aborting process", urlForExternalIP);
                    break;
                }
                else
                    _logger.LogInformation("First 50char in url response: {urlResp}", httpStringContent[.. (httpStringContent.Length < 50 ? httpStringContent.Length:50)]);
                string ip;
                if (string.IsNullOrWhiteSpace(regexToParseURLResponse))
                    ip = httpStringContent;
                else
                {
                    var baseRegex = new Regex(regexToParseURLResponse, RegexOptions.IgnoreCase);
                    var regexMatches = baseRegex.Count(httpStringContent);
                    if (regexMatches > 1)
                    {
                        if (_logger.IsEnabled(LogLevel.Information))
                            _logger.LogInformation("Multiple IP address matching regex found. using the first");
                    }
                    else if (regexMatches == 0)
                    {
                        _logger.LogError("No IP Address found matching regex. aborting process");
                        break;
                    }
                    ip = baseRegex.Match(httpStringContent).Value;
                }


                if (_logger.IsEnabled(LogLevel.Information))
                {
                    _logger.LogInformation("Last ip is '{lastIP}' current ip is '{ip}'. running at: {time}", lastIP, ip, DateTimeOffset.Now);
                }
                if (ip != lastIP)
                {
                    bool isIPValid = IPAddress.TryParse(ip, out IPAddress? parsedIP);
                    if (!isIPValid)
                    {
                        _logger.LogInformation("IP Parsed is not a valid ip");
                        break;
                    }
                    var sub = armclient.GetSubscriptionResource(new ResourceIdentifier($"/subscriptions/{azureSub}"));
                    if (sub == null) break;
                    var resourceGroup = await sub.GetResourceGroupAsync(azureResourceName, stoppingToken);
                    if (resourceGroup == null || !resourceGroup.Value.HasData) break;
                    var dnszones = resourceGroup.Value.GetDnsZones();
                    if (dnszones == null || !dnszones.Any()) break;
                    var zone = dnszones.FirstOrDefault(x => x.Data.Name == dnsZoneName);
                    if (zone == null || !zone.HasData) break;

                    //We should have data for our supplied stuff. now we need to update our anameRecord
                    var dnsRecord = await zone.GetDnsARecordAsync(dnsARecordToUpdate, stoppingToken);
                    if (dnsRecord == null || !dnsRecord.Value.HasData) break;

                    var temp = dnsRecord.Value.Data;

                    if (temp.DnsARecords.Any(x => x.IPv4Address.ToString() == parsedIP!.ToString()))
                    {
                        if (_logger.IsEnabled(LogLevel.Information))
                            _logger.LogInformation($"Existing Cloud IP was same as current. doing nothing");
                    }
                    else if (temp.DnsARecords.Any())
                    {
                        if (_logger.IsEnabled(LogLevel.Information))
                            _logger.LogInformation("Existing Cloud IP was '{existingdns}'  new ip address is {ip}", temp.DnsARecords.FirstOrDefault()!.IPv4Address, ip);
                        temp.DnsARecords.Clear();
                        temp.DnsARecords.Add(new Azure.ResourceManager.Dns.Models.DnsARecordInfo() { IPv4Address = parsedIP });
                        temp.TtlInSeconds = dnsARecordTTL;
                        await dnsRecord.Value.UpdateAsync(temp, cancellationToken: stoppingToken);
                        if (_logger.IsEnabled(LogLevel.Information))
                            _logger.LogInformation($"Azure A Record Updated");
                    }
                    else
                    {
                        //here i need to actually create the DNS Record cause it didnt exist beforehand
                        _logger.LogInformation("Did not find an existing DNS A Record for: {dnsARecordToUpdate}, Will create new record.", dnsARecordToUpdate);
                        var allDnsRecords = zone.GetDnsARecords();
                        var dnsARecord = new DnsARecordData()
                        {
                            TtlInSeconds = dnsARecordTTL,
                        };
                        dnsARecord.DnsARecords.Add(new DnsARecordInfo() { IPv4Address = parsedIP });

                        var recordUpdateResult = await allDnsRecords.CreateOrUpdateAsync(Azure.WaitUntil.Completed, dnsARecordToUpdate, dnsARecord, cancellationToken: stoppingToken);
                        recordUpdateResult.WaitForCompletion(stoppingToken);
                        int breakcounter = 0;
                        while (!recordUpdateResult.HasCompleted && breakcounter<6)
                        {
                            await Task.Delay(2000, stoppingToken);
                            breakcounter++;
                        }
                        if (recordUpdateResult.HasCompleted)
                        {
                            _logger.LogInformation("DNS A Record for: {dnsARecordToUpdate}, Has been created", dnsARecordToUpdate);
                        }
                        else
                        {
                            _logger.LogInformation("Creation for dns A Record: {dnsARecordToUpdate} took too long. it may be done.", dnsARecordToUpdate);
                        }
                    }
                }

                lastIP = ip;
                await Task.Delay(pollFrequency, stoppingToken);
            }
        }

        private bool IsConfigInValid()
        {
            return (string.IsNullOrWhiteSpace(azureTenant)
              || string.IsNullOrWhiteSpace(azureClientID)
              || string.IsNullOrWhiteSpace(azureClientSecret)
              || string.IsNullOrWhiteSpace(azureSub)
              || string.IsNullOrWhiteSpace(azureResourceName)
              || string.IsNullOrWhiteSpace(dnsZoneName)
              || string.IsNullOrWhiteSpace(urlForExternalIP));
        }
    }

}
