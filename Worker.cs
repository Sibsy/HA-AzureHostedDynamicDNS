using Azure.Core;
using Azure.Identity;
using Azure.ResourceManager;
using Azure.ResourceManager.Dns;
using Azure.ResourceManager.Dns.Models;
using Azure.ResourceManager.Resources;
using System.Net;
using System.Text.RegularExpressions;

namespace Azure_Hosted_Dynamic_DNS
{
    public class Worker : BackgroundService
    {
        private string lastIP = string.Empty;
        private ArmClient? armclient = null;
        private readonly ILogger<Worker> _logger;
        private IPAddress? _ipAddress;

        private readonly static string? azureTenant = Environment.GetEnvironmentVariable("AzureTenantID");
        private readonly static string? azureClientID = Environment.GetEnvironmentVariable("AzureClientID");
        private readonly static string? azureClientSecret = Environment.GetEnvironmentVariable("AzureClientSecret");
        private readonly static string? azureSub = Environment.GetEnvironmentVariable("AzureSubID");
        private readonly static string? azureResourceName = Environment.GetEnvironmentVariable("AzureResourceName");
        private readonly static string? dnsZoneName = Environment.GetEnvironmentVariable("DNSZoneName");
        private readonly static string? urlForExternalIP = Environment.GetEnvironmentVariable("IPServiceURL");
        private readonly static string? regexToParseURLResponse = Environment.GetEnvironmentVariable("URLParsingRegex");

        private static readonly string? baseDNSARecord = Environment.GetEnvironmentVariable("DNSARecordToUpdate");
        private readonly static string dnsARecordToUpdate = baseDNSARecord ?? "HA";

        private readonly static int pollFrequency = int.TryParse(Environment.GetEnvironmentVariable("PollFrequency"), out int parsedPollFreq) ? parsedPollFreq : 300000;
        private readonly static int dnsARecordTTL = int.TryParse(Environment.GetEnvironmentVariable("DNSARecordTTL"), out int parsedTTL) ? parsedTTL : 300;

        public Worker(ILogger<Worker> logger)
        {
            _logger = logger;
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation("Worker has Started.");
            }
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (IsConfigInValid())
                return;

            SetupArmClient();

            StartupLogging();

            while (!stoppingToken.IsCancellationRequested)
            {

                string? ip = await GetExternalIPAsync(stoppingToken);
                if (string.IsNullOrWhiteSpace(ip))
                    return;

                _logger.LogInformation("Last ip is '{lastIP}' current ip is '{ip}'. running at: {time}", lastIP, ip, DateTimeOffset.Now);

                if (ip != lastIP)
                {
                    bool isIPValid = IPAddress.TryParse(ip, out _ipAddress);
                    if (!isIPValid)
                    {
                        _logger.LogInformation("IP Parsed is not a valid ip");
                        break;
                    }

                    var zone = await GetDNSZoneAsync(stoppingToken);
                    if (zone == null || !zone.HasData) break;
                    await ValidateAndUpdateDNSRecords(zone,stoppingToken);
                }
                else
                    _logger.LogInformation("Last IP was same as Current IP, Doing Nothing.");
                lastIP = ip;
                await Task.Delay(pollFrequency, stoppingToken);
            }
        }
        private void SetupArmClient()
        {
            if (armclient == null)
            {
                ClientSecretCredential? cred = new(azureTenant, azureClientID, azureClientSecret);
                armclient = new ArmClient(cred);
            }
        }

        private bool IsConfigInValid()
        {
            bool result = (string.IsNullOrWhiteSpace(azureTenant)
              || string.IsNullOrWhiteSpace(azureClientID)
              || string.IsNullOrWhiteSpace(azureClientSecret)
              || string.IsNullOrWhiteSpace(azureSub)
              || string.IsNullOrWhiteSpace(azureResourceName)
              || string.IsNullOrWhiteSpace(dnsZoneName)
              || string.IsNullOrWhiteSpace(urlForExternalIP));

            if (result)
                _logger.LogError("Missing Config, Please Ensure supplied config is correct and present.");
            return result;
        }

        private void StartupLogging()
        {
            _logger.LogInformation("Configuration Used: ");
            _logger.LogInformation("DNSZoneName: {dnsZoneName}", dnsZoneName);
            _logger.LogInformation("DNSARecordToUpdate: {dnsARecordToUpdate}", dnsARecordToUpdate);
            _logger.LogInformation("Poll Frequency: {pollFrequency}", pollFrequency);
            _logger.LogInformation("DNSARecordTTL: {dnsARecordTTL} ", dnsARecordTTL);
            _logger.LogInformation("URL For Getting External IP: {urlForExternalIP}", urlForExternalIP);
            _logger.LogInformation("Regex for IP Parsing: {regexToParseURLResponse}", regexToParseURLResponse);
        }

        private async Task<string?> GetExternalIPAsync(CancellationToken stoppingToken)
        {

            using var client = new HttpClient();
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
                return null;
            }
            else
                _logger.LogInformation("First 50char in url response: {urlResp}", httpStringContent[..(httpStringContent.Length < 50 ? httpStringContent.Length : 50)]);
            if (string.IsNullOrWhiteSpace(regexToParseURLResponse))
                return httpStringContent;
            if (ParseResponseToIPString(httpStringContent, out string? ParsedResult))
                return ParsedResult;
            else
                return null;

        }

        private bool ParseResponseToIPString(string httpStringContent, out string? ParsedResult)
        {
            var baseRegex = new Regex(regexToParseURLResponse!, RegexOptions.IgnoreCase, matchTimeout: new TimeSpan(0, 0, 5));
            var regexMatches = baseRegex.Count(httpStringContent);
            if (regexMatches > 1)
                _logger.LogInformation("Multiple IP address matching regex found. using the first");
            else if (regexMatches == 0)
            {
                _logger.LogError("No IP Address found matching regex. aborting process");
                ParsedResult = null;
                return false;
            }
            ParsedResult = baseRegex.Match(httpStringContent).Value;
            return true;
        }

        private async Task CreateDNSRecord(DnsARecordCollection records, CancellationToken stoppingToken)
        {
            _logger.LogInformation("Did not find an existing DNS A Record for: {dnsARecordToUpdate}, Will create new record.", dnsARecordToUpdate);
            var dnsARecord = new DnsARecordData()
            {
                TtlInSeconds = dnsARecordTTL,
            };
            dnsARecord.DnsARecords.Add(new DnsARecordInfo() { IPv4Address = _ipAddress });

            var recordUpdateResult = await records.CreateOrUpdateAsync(Azure.WaitUntil.Completed, dnsARecordToUpdate, dnsARecord, cancellationToken: stoppingToken);
            recordUpdateResult.WaitForCompletion(stoppingToken);
            int breakcounter = 0;
            while (!recordUpdateResult.HasCompleted && breakcounter < 6)
            {
                await Task.Delay(2000, stoppingToken);
                breakcounter++;
            }
            if (recordUpdateResult.HasCompleted)
                _logger.LogInformation("DNS A Record for: {dnsARecordToUpdate}, Has been created", dnsARecordToUpdate);
            else
                _logger.LogInformation("Creation for dns A Record: {dnsARecordToUpdate} took too long. it may be done.", dnsARecordToUpdate);
        }
        
        private async Task ValidateAndUpdateDNSRecords(DnsZoneResource dnsZone, CancellationToken stoppingToken)
        {
            //We should have data for our supplied stuff. now we need to update our anameRecord
            var dnsRecord = await dnsZone.GetDnsARecordAsync(dnsARecordToUpdate, stoppingToken);
            if (dnsRecord == null || !dnsRecord.Value.HasData)
                await CreateDNSRecord(dnsZone.GetDnsARecords(), stoppingToken);
            else
            {
                var recordData = dnsRecord.Value.Data;
                if (recordData.DnsARecords.Any(x => x.IPv4Address.ToString() == _ipAddress!.ToString()))
                    _logger.LogInformation("Existing Cloud IP was same as current. doing nothing");
                else if (recordData.DnsARecords.Any())
                {
                    _logger.LogInformation("Existing Cloud IP was '{existingdns}'  new ip address is {ip}", recordData.DnsARecords.FirstOrDefault()!.IPv4Address, _ipAddress);
                    recordData.DnsARecords.Clear();
                    recordData.DnsARecords.Add(new DnsARecordInfo() { IPv4Address = _ipAddress });
                    recordData.TtlInSeconds = dnsARecordTTL;
                    await dnsRecord.Value.UpdateAsync(recordData, cancellationToken: stoppingToken);
                    _logger.LogInformation("Azure A Record Updated");
                }
            }
        }

        private async Task<DnsZoneResource?> GetDNSZoneAsync(CancellationToken stoppingToken)
        {
            SubscriptionResource? sub = armclient!.GetSubscriptionResource(new ResourceIdentifier($"/subscriptions/{azureSub}"));
            if (sub == null) return null;
            var resourceGroup = await sub.GetResourceGroupAsync(azureResourceName, stoppingToken);
            if (resourceGroup == null || !resourceGroup.Value.HasData) return null;
            var dnszones = resourceGroup.Value.GetDnsZones();
            if (dnszones == null || !dnszones.Any()) return null;
            return dnszones.FirstOrDefault(x => x.Data.Name == dnsZoneName);
        }
    }

}
