using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using DigitalOcean.API;

namespace DigitalOcean.DNS.ImportExport
{
    class Program
    {
        static void Main(string[] args)
        {
            try
            {
                string sourceToken = null;
                string targetToken = null;

                foreach (var arg in args)
                {
                    var parts = arg.Split("=", StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length == 2)
                    {
                        if (parts[0].ToLowerInvariant().StartsWith("source", StringComparison.InvariantCultureIgnoreCase))
                            sourceToken = parts[1];
                        else if (parts[0].ToLowerInvariant().StartsWith("target", StringComparison.InvariantCultureIgnoreCase))
                            targetToken = parts[1];
                    }
                }

                sourceToken = ReadLine.Read("Enter source DO account token (to read DNS info from): [null]", null);
                while (string.IsNullOrWhiteSpace(targetToken))
                {
                    targetToken = ReadLine.Read("Enter target DO account token (to create new DNS records): ");
                }

                AsyncCall(sourceToken, targetToken).Wait();
            }
            catch (Exception ex)
            {
                Write($"There was an exception: {ex.ToString()}");
            }
        }

        private static async Task AsyncCall(string sourceToken, string targetToken)
        {
            while (true)
            {
                if (!string.IsNullOrWhiteSpace(sourceToken))
                    Write($"Source Client Id: {sourceToken.Remove(15)}...");

                if (string.IsNullOrWhiteSpace(targetToken))
                    throw new Exception("Target DO account token should not be null.");

                Write($"Target Client Id: {targetToken.Remove(15)}...");

                var sourceClient = string.IsNullOrWhiteSpace(sourceToken) ? null : new DigitalOceanClient(sourceToken);
                var targetClient = new DigitalOceanClient(targetToken);

                var action = ReadLine.Read($"What do you want to do ({(sourceClient == null ? "" : "copy / ")} import / quit) [quit]? ", "quit");

                if (action.Equals("quit", StringComparison.InvariantCultureIgnoreCase))
                    break;

                IEnumerable<API.Models.Responses.DomainRecord> sourceDomainRecords;
                string selectedTargetDomain;
                if (sourceClient != null && action.Equals("copy", StringComparison.InvariantCultureIgnoreCase))
                {
                    var sourceDomains = await sourceClient.Domains.GetAll();

                    ReadLine.AutoCompletionHandler = new AutoCompletionHandler(sourceDomains.Select(x => x.Name));

                    Write("Source domains:");
                    foreach (var sourceDomain in sourceDomains)
                    {
                        //Write($"{sourceDomain.Name} : {sourceDomain.ZoneFile}");
                        Write($"{sourceDomain.Name}");
                        ReadLine.AddHistory(sourceDomain.Name);
                    }

                    var selectedSourceDomain = ReadLine.Read("Enter source domain [quit]: ", "quit");

                    if (selectedSourceDomain.Equals("quit", StringComparison.InvariantCultureIgnoreCase))
                        break;

                    sourceDomainRecords = await sourceClient.DomainRecords.GetAll(selectedSourceDomain);

                    Write("Source domains records:");
                    foreach (var sourceDomainRecord in sourceDomainRecords)
                    {
                        Write(PrintDomainRecord(sourceDomainRecord));
                    }
                    selectedTargetDomain = ReadLine.Read($"Enter target domain [{selectedSourceDomain}]: ", selectedSourceDomain);

                    if (selectedTargetDomain.Equals(selectedSourceDomain, StringComparison.InvariantCultureIgnoreCase))
                    {
                        var removeSourceDomain = ReadLine.Read("Source and Target domains are same. Should we remove source domain (y/n) [y]? ", "y");
                        if (removeSourceDomain.ToLower() == "y")
                        {
                            await sourceClient.Domains.Delete(selectedSourceDomain);
                            Write($"Successfully deleted {selectedSourceDomain} from Source DO account.");
                        }
                    }
                }
                else if (action.Equals("import", StringComparison.InvariantCultureIgnoreCase))

                {
                    selectedTargetDomain = ReadLine.Read($"Enter target domain: ");

                    Write(
@"
DNS Records format:
   NS: WILL GET IGNORED!
    A: arcwebsite.com       A       138.197.169.62
 AAAA: arcwebsite.com       AAAA    2001:0db8:85a3:0000:0000:8a2e:0370:7334
CNAME: www.website.com      CNAME   website.com.
  CAA: arcwebsite.com       CAA     0 issue ""entrust.net""
   MX: arcwebsite.com       MX      10 mail.arcwebsite.com.
  TXT: arcwebsite.com       TXT     v=spf1 mx -all
  SRV: _caldavs._tcp.a.com  SRV     0 0 443 mail.arcwebsite.com.
                    ");

                    var dnsRecordsToInsert = new List<string>();

                    while (true)
                    {
                        var oneDnsRecordsToInsert = ReadLine.Read($"Enter DNS records [got {dnsRecordsToInsert.Count()} so far] (type end to finish): ");

                        if (oneDnsRecordsToInsert.Equals("end", StringComparison.InvariantCultureIgnoreCase))
                            break;

                        dnsRecordsToInsert.Add(oneDnsRecordsToInsert);
                    }

                    sourceDomainRecords = new List<API.Models.Responses.DomainRecord>();

                    Write("Start parsing...");
                    foreach (var oneDnsRecordsToInsert in dnsRecordsToInsert)
                    {
                        try
                        {
                            Write($"Parse: {oneDnsRecordsToInsert}", false);
                            var newDomainRecord = ParseDomainRecord(oneDnsRecordsToInsert, selectedTargetDomain);

                            if (newDomainRecord == null)
                            {
                                Write("Returned NULL");
                                continue;
                            }

                            ((List<API.Models.Responses.DomainRecord>)sourceDomainRecords)
                                .Add(newDomainRecord);
                            Write(" -> DONE!");
                            Write($"Parsed Value: {PrintDomainRecord(newDomainRecord)}");
                        }
                        catch (Exception ex)
                        {
                            Write(" -> FAIL!");
                            Write($" Unable to create domain record: {ex.Message}");
                        }
                    }
                }
                else
                {
                    Write($"Unsupported Action: {action}");
                    continue;
                }

                var confirmation = ReadLine.Read("Do you wish to continue and create NS records [continue]? ", "continue");
                if (!confirmation.Equals("continue", StringComparison.InvariantCultureIgnoreCase))
                {
                    Write("Restarting App...");
                    continue;
                }

                API.Models.Responses.Domain checkTargetDomain = null;

                try
                {
                    checkTargetDomain = await targetClient.Domains.Get(selectedTargetDomain);
                }
                catch
                {
                    Write("Target domain not found on target DO account... trying to create.");
                    checkTargetDomain = await targetClient.Domains.Create(new DigitalOcean.API.Models.Requests.Domain
                    {
                        Name = selectedTargetDomain
                    });
                }

                if (checkTargetDomain == null)
                    throw new Exception("Target domain not found.");

                foreach (var sourceDomainRecord in sourceDomainRecords)
                {
                    if (sourceDomainRecord.Type == "NS")
                        continue;

                    try
                    {
                        var requestDomainRecord = new DigitalOcean.API.Models.Requests.DomainRecord()
                        {
                            Name = sourceDomainRecord.Name,
                            Data = (sourceDomainRecord.Type.Equals("TXT", StringComparison.InvariantCultureIgnoreCase) || sourceDomainRecord.Type.Equals("CAA", StringComparison.InvariantCultureIgnoreCase))
                                      ? sourceDomainRecord.Data
                                      : ProcessData(sourceDomainRecord.Data),
                            Port = sourceDomainRecord.Port,
                            Priority = sourceDomainRecord.Priority,
                            TTL = sourceDomainRecord.TTL,
                            Type = sourceDomainRecord.Type,
                            Weight = sourceDomainRecord.Weight,
                            Flags = sourceDomainRecord.Flags,
                            Tag = sourceDomainRecord.Tag
                        };

                        Write($"Creating: {PrintDomainRecord(requestDomainRecord)}", false);

                        var x = await targetClient.DomainRecords.Create(checkTargetDomain.Name, requestDomainRecord);

                        Write(" -> DONE!");
                    }
                    catch (Exception ex)
                    {
                        Write(" -> FAIL!");
                        Write($" Unable to create domain record: {ex.Message}");
                        Write($"{"".PadLeft(20, '-')}\r\n{PrintDomainRecord(sourceDomainRecord)}\r\n{"".PadRight(20, '-')}");
                    }
                }
            }

            Write("Thanks for using Digital Ocean DNS Import/Export");
        }

        private static string PrintDomainRecord(API.Models.Responses.DomainRecord domainRecord)
        {
            //    NS: IGNORE!
            //     A: arcwebsite.com       A       138.197.169.62
            //  AAAA: arcwebsite.com       AAAA    2001:0db8:85a3:0000:0000:8a2e:0370:7334
            // CNAME: www.website.com      CNAME   website.com.
            //   CAA: arcwebsite.com       CAA     0 issue "entrust.net"
            //    MX: arcwebsite.com       MX      10 mail.arcwebsite.com.
            //   TXT: arcwebsite.com       TXT     v=spf1 mx -all
            //   SRV: _caldavs._tcp.a.com  SRV     0 0 443 mail.arcwebsite.com.

            switch (domainRecord.Type)
            {
                case "A":
                case "AAAA":
                case "CNAME":
                case "TXT":
                case "NS":
                    return $"{domainRecord.Type.PadLeft(7)}:\t{domainRecord.Name}\t{domainRecord.Type}\t{domainRecord.Data}\t[TTL: {domainRecord.TTL?.ToString() ?? "default"}]";
                case "CAA":
                    return $"{domainRecord.Type.PadLeft(7)}:\t{domainRecord.Name}\t{domainRecord.Type}\t{domainRecord.Flags} {domainRecord.Tag} {domainRecord.Data}\t[TTL: {domainRecord.TTL?.ToString() ?? "default"}]";
                case "MX":
                    return $"{domainRecord.Type.PadLeft(7)}:\t{domainRecord.Name}\t{domainRecord.Type}\t{domainRecord.Priority} {domainRecord.Data}\t[TTL: {domainRecord.TTL?.ToString() ?? "default"}]";
                case "SRV":
                    return $"{domainRecord.Type.PadLeft(7)}:\t{domainRecord.Name}\t{domainRecord.Type}\t{domainRecord.Priority} {domainRecord.Weight} {domainRecord.Port} {domainRecord.Data}\t[TTL: {domainRecord.TTL?.ToString() ?? "default"}]";
                default:
                    return null;
            }
        }


        private static string PrintDomainRecord(API.Models.Requests.DomainRecord domainRecord)
        {
            //    NS: IGNORE!
            //     A: arcwebsite.com       A       138.197.169.62
            //  AAAA: arcwebsite.com       AAAA    2001:0db8:85a3:0000:0000:8a2e:0370:7334
            // CNAME: www.website.com      CNAME   website.com.
            //   CAA: arcwebsite.com       CAA     0 issue "entrust.net"
            //    MX: arcwebsite.com       MX      10 mail.arcwebsite.com.
            //   TXT: arcwebsite.com       TXT     v=spf1 mx -all
            //   SRV: _caldavs._tcp.a.com  SRV     0 0 443 mail.arcwebsite.com.

            switch (domainRecord.Type)
            {
                case "A":
                case "AAAA":
                case "CNAME":
                case "TXT":
                case "NS":
                    return $"{domainRecord.Type.PadLeft(7)}:\t{domainRecord.Name}\t{domainRecord.Type}\t{domainRecord.Data}\t[TTL: {domainRecord.TTL?.ToString() ?? "default"}]";
                case "CAA":
                    return $"{domainRecord.Type.PadLeft(7)}:\t{domainRecord.Name}\t{domainRecord.Type}\t{domainRecord.Flags} {domainRecord.Tag} {domainRecord.Data}\t[TTL: {domainRecord.TTL?.ToString() ?? "default"}]";
                case "MX":
                    return $"{domainRecord.Type.PadLeft(7)}:\t{domainRecord.Name}\t{domainRecord.Type}\t{domainRecord.Priority} {domainRecord.Data}\t[TTL: {domainRecord.TTL?.ToString() ?? "default"}]";
                case "SRV":
                    return $"{domainRecord.Type.PadLeft(7)}:\t{domainRecord.Name}\t{domainRecord.Type}\t{domainRecord.Priority} {domainRecord.Weight} {domainRecord.Port} {domainRecord.Data}\t[TTL: {domainRecord.TTL?.ToString() ?? "default"}]";
                default:
                    return null;
            }
        }

        private static void Write(string message, bool newline = true)
        {
            if (newline)
                Console.WriteLine(message);
            else
                Console.Write(message);
        }

        private static string ProcessData(string data)
        {
            if (data.Equals("@"))
                return data;

            if (IPAddress.TryParse(data, out IPAddress tempAddress))
                return data;

            if (!data.EndsWith('.'))
                return data + ".";

            return data;
        }

        private static API.Models.Responses.DomainRecord ParseDomainRecord(string inputString, string domainName)
        {
            var parts = inputString.Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length < 3)
                throw new Exception($"Unable to parse {inputString}");

            var type = parts[1].ToUpper();

            if (!new[] { "A", "AAAA", "CAA", "CNAME", "MX", "NS", "TXT", "SRV" }.Contains(type))
                throw new Exception($"Unsupported type: {type}");

            var result = new API.Models.Responses.DomainRecord();
            result.Type = type;
            result.Name = parts[0];

            if (result.Name.EndsWith(domainName, StringComparison.InvariantCultureIgnoreCase))
                result.Name = result.Name.Remove(result.Name.Length - domainName.Length).TrimEnd('.');

            if (string.IsNullOrEmpty(result.Name))
                result.Name = "@";


            //    NS: IGNORE!
            //     A: arcwebsite.com       A       138.197.169.62
            //  AAAA: arcwebsite.com       AAAA    2001:0db8:85a3:0000:0000:8a2e:0370:7334
            // CNAME: www.website.com      CNAME   website.com.
            //   CAA: arcwebsite.com       CAA     0 issue "entrust.net"
            //    MX: arcwebsite.com       MX      10 mail.arcwebsite.com.
            //   TXT: arcwebsite.com       TXT     v=spf1 mx -all
            //   SRV: _caldavs._tcp.a.com  SRV     0 0 443 mail.arcwebsite.com.

            switch (type)
            {
                case "A":
                case "AAAA":
                case "CNAME":
                case "TXT":
                    if (parts.Length < 3)
                        throw new Exception("Invalid input parts length");
                    result.Data = MergeParts(parts, 2, parts.Length);
                    break;
                case "CAA":
                    if (parts.Length < 5)
                        throw new Exception("Invalid input parts length");
                    if (Int32.TryParse(parts[2], out int flags))
                        result.Flags = flags;
                    else
                        throw new Exception("Unable to parse flag for CAA record.");
                    result.Tag = parts[3];
                    result.Data = MergeParts(parts, 4, parts.Length);
                    break;
                case "MX":
                    if (parts.Length < 4)
                        throw new Exception("Invalid input parts length");
                    if (Int32.TryParse(parts[2], out int mxpriority))
                        result.Priority = mxpriority;
                    else
                        throw new Exception("Unable to parse priority for MX record.");
                    result.Data = MergeParts(parts, 3, parts.Length);
                    break;
                case "SRV":
                    if (parts.Length < 6)
                        throw new Exception("Invalid input parts length");
                    if (Int32.TryParse(parts[2], out int srvpriority))
                        result.Priority = srvpriority;
                    else
                        throw new Exception("Unable to parse priority for SRV record.");
                    if (Int32.TryParse(parts[3], out int weight))
                        result.Weight = weight;
                    else
                        throw new Exception("Unable to parse weight for SRV record.");
                    if (Int32.TryParse(parts[4], out int port))
                        result.Port = port;
                    else
                        throw new Exception("Unable to parse port for SRV record.");
                    result.Data = MergeParts(parts, 5, parts.Length);
                    break;
                case "NS":
                default:
                    return null;
            }

            return result;
        }

        private static string MergeParts(string[] parts, int from, int to, string separator = " ")
        {
            var result = string.Empty;
            if (parts.Length < to)
                throw new Exception("Parts length should be larger than or equal to TO parameter");
            for (int i = from; i < to; i++)
            {
                result = result + parts[i] + (i < to - 1 ? separator : "");
            }

            return result;
        }

        class AutoCompletionHandler : IAutoCompleteHandler
        {
            private IEnumerable<string> _domainsList { get; }

            public AutoCompletionHandler(IEnumerable<string> domainsList)
            {
                _domainsList = domainsList;
            }

            // characters to start completion from
            public char[] Separators { get; set; } = new char[] { ' ', '.', '/' };

            // text - The current text entered in the console
            // index - The index of the terminal cursor within {text}
            public string[] GetSuggestions(string text, int index)
            {
                return _domainsList.Where(x => x.StartsWith(text, StringComparison.InvariantCultureIgnoreCase)).ToArray();
            }
        }

    }
}
