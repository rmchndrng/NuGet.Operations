﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Data.SqlClient;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Dapper;
using Microsoft.WindowsAzure;
using Microsoft.WindowsAzure.Management.Compute.Models;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Blob;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Serialization;
using NuCmd.Models;
using NuGet;
using NuGet.Services;
using NuGet.Services.Client;
using NuGet.Services.Operations.Model;
using PowerArgs;

namespace NuCmd.Commands.Package
{
    [Description("Populate the Package Frameworks index in the database from the data in the package itself")]
    public class PopulatePackageFrameworksCommand : AzureCommandBase
    {
        [ArgPosition(0)]
        [ArgShortcut("i")]
        [ArgDescription("The ID of the package to process when processing only a single package registration")]
        public string Id { get; set; }

        [ArgPosition(1)]
        [ArgShortcut("v")]
        [ArgDescription("The Version of the package to process")]
        public string Version { get; set; }

        [ArgShortcut("a")]
        [ArgDescription("Set this flag to process all versions of the package when the ID is specified, or all packages if ID is not specified.")]
        public bool All { get; set; }

        [ArgShortcut("db")]
        [ArgDescription("SQL Connection string for the package database.")]
        public string DatabaseConnectionString { get; set; }

        [ArgShortcut("st")]
        [ArgDescription("Azure Storage Connection string for the package storage.")]
        public string StorageConnectionString { get; set; }

        [ArgShortcut("work")]
        [ArgDescription("Directory in which to put resume data and other work")]
        public string WorkDirectory { get; set; }

        private CloudStorageAccount StorageAccount { get; set; }

        private static readonly int _padLength = Enum.GetValues(typeof(PackageReportState)).Cast<PackageReportState>().Select(p => p.ToString().Length).Max();

        private static readonly JsonSerializer _serializer = new JsonSerializer()
        {
            ContractResolver = new CamelCasePropertyNamesContractResolver(),
            TypeNameHandling = TypeNameHandling.None,
            Formatting = Formatting.Indented,
            DateFormatHandling = DateFormatHandling.IsoDateFormat,
            DateTimeZoneHandling = DateTimeZoneHandling.Utc,
        };

        protected override async Task OnExecute()
        {
            if (String.IsNullOrWhiteSpace(Version) && !All)
            {
                await Console.WriteErrorLine(Strings.Package_AllVersionsRequiredIfVersionNull);
                return;
            }

            // Get Datacenter 0
            var dc = GetDatacenter(0, required: false);
            if (dc != null)
            {
                await LoadDefaultsFromAzure(dc);
            }

            StorageAccount = CloudStorageAccount.Parse(StorageConnectionString);

            // Parse the version
            if (!String.IsNullOrWhiteSpace(Version))
            {
                Version = SemanticVersionHelper.Normalize(Version);
            }

            if (!String.IsNullOrWhiteSpace(Version) && All)
            {
                await Console.WriteErrorLine(Strings.Package_VersionAndAllVersionsSpecified);
                return;
            }

            // Get all of the packages to be processed
            using (var conn = new SqlConnection(DatabaseConnectionString))
            {
                await conn.OpenAsync();
                var packages = conn.Query(@"
                    SELECT      p.[Key], pr.Id, p.Version, p.Hash, p.Created
                    FROM        Packages p
                    INNER JOIN  PackageRegistrations pr
                            ON  pr.[Key] = p.PackageRegistrationKey
                    WHERE       (NullIf('', @Id) IS NULL OR pr.Id = @Id)
                            AND (@All = 1 OR p.NormalizedVersion = @Version)
                    ORDER BY    p.Created DESC", new
                    {
                        Id,
                        All,
                        Version
                    });

                if (!WhatIf)
                {
                    bool confirmed = await Console.Confirm(
                        String.Format(
                            Strings.Package_PopulatePackageFrameworksCommand_Confirm,
                            packages.Count(),
                            (dc == null ? "<unknown>" : dc.FullName)),
                        defaultValue: true);

                    if (!confirmed)
                    {
                        await Console.WriteErrorLine(Strings.SystemConsole_ConfirmDenied);
                        return;
                    }
                }

                int totalCount = packages.Count();
                int processedCount = 0;

                packages
                    .AsParallel()
                    .AsOrdered()
                    .WithDegreeOfParallelism(10)
                    .ForAll(package => 
                    {
                        var thisPackageId = Interlocked.Increment(ref processedCount);

                        try
                        {
                            var reportPath = Path.Combine(WorkDirectory, package.Id + "_" + package.Version + ".json");
                            var bustedReportPath = Path.Combine(WorkDirectory, package.Id + "_" + package.Version + "_" + package.Hash + ".json");

                            var report = new PackageFrameworkReport()
                            {
                                Id = package.Id,
                                Version = package.Version,
                                Key = package.Key,
                                Hash = package.Hash,
                                Created = package.Created.Value,
                                State = PackageReportState.Unresolved
                            };

                            if (File.Exists(bustedReportPath))
                            {
                                File.Move(bustedReportPath, reportPath);
                            }

                            if (File.Exists(reportPath))
                            {
                                using (var reader = File.OpenText(reportPath))
                                {
                                    report = (PackageFrameworkReport)_serializer.Deserialize(
                                        reader, typeof(PackageFrameworkReport));
                                    
                                    ResolveReport(report);
                                }
                            }
                            else
                            {
                                try
                                {
                                    var downloadPath = DownloadPackage(package);
                                    var nugetPackage = new ZipPackage(downloadPath);

                                    var supportedFrameworks = GetSupportedFrameworks(nugetPackage);
                                    report.PackageFrameworks = supportedFrameworks.ToArray();
                                    PopulateFrameworks(report, package);

                                    File.Delete(downloadPath);

                                    ResolveReport(report);
                                }
                                catch (Exception ex)
                                {
                                    report.State = PackageReportState.Error;
                                    report.Error = ex.ToString();
                                }
                            }

                            using (var writer = File.CreateText(reportPath))
                            {
                                _serializer.Serialize(writer, report);
                            }

                            Console.WriteInfoLine("[{2}/{3} {4}%] {6} Package: {0}@{1} (created {5})",
                                (string)package.Id,
                                (string)package.Version,
                                thisPackageId.ToString("0000000"),
                                totalCount.ToString("0000000"),
                                (((double)thisPackageId / (double)totalCount) * 100).ToString("000.00"),
                                (DateTime)package.Created.Value,
                                report.State.ToString().PadRight(_padLength, ' '));
                        }
                        catch (Exception ex)
                        {
                            Console.WriteErrorLine("[{2}/{3} {4}%] Error for Package: {0}@{1}: {5}",
                                (string)package.Id,
                                (string)package.Version,
                                thisPackageId.ToString("0000000"),
                                totalCount.ToString("0000000"),
                                (((double)thisPackageId / (double)totalCount) * 100).ToString("000.00"),
                                ex.ToString());
                        }
                    });
            }
        }

        private void ResolveReport(PackageFrameworkReport report)
        {
            bool error = false;
            using (var sqlConnection = new SqlConnection(ConnectionString.ConnectionString))
            using (var dbExecutor = new SqlExecutor(sqlConnection))
            {
                sqlConnection.Open();
                foreach (var operation in report.Operations)
                {
                    if (!WhatIf)
                    {
                        if (operation.Type == PackageFrameworkOperationType.Add)
                        {
                            try
                            {
                                dbExecutor.Execute(@"
                                    INSERT INTO PackageFrameworks(TargetFramework, Package_Key)
                                    VALUES (@targetFramework, @packageKey)",
                                        new
                                        {
                                            targetFramework = operation.Framework,
                                            packageKey = report.Key
                                        });
                                Log.Info(" + Id={0}, Key={1}, Fx={2}", report.Id, report.Key, operation.Framework);
                                operation.Applied = true;
                            }
                            catch (Exception ex)
                            {
                                error = true;
                                operation.Applied = false;
                                operation.Error = ex.ToString();
                            }
                        }
                        else if (operation.Type == PackageFrameworkOperationType.Remove)
                        {
                            try
                            {
                                dbExecutor.Execute(@"
                                    DELETE FROM PackageFrameworks
                                    WHERE TargetFramework = @targetFramework AND Package_Key = @packageKey",
                                        new
                                        {
                                            targetFramework = operation.Framework,
                                            packageKey = report.Key
                                        });
                                Log.Info(" - Id={0}, Key={1}, Fx={2}", report.Id, report.Key, operation.Framework);
                                operation.Applied = true;
                            }
                            catch (Exception ex)
                            {
                                error = true;
                                operation.Applied = false;
                                operation.Error = ex.ToString();
                            }
                        }
                    }
                }
            }

            if (error)
            {
                report.State = PackageReportState.Error;
            }
            else if (report.Operations.All(o => o.Applied))
            {
                report.State = PackageReportState.Resolved;
            }
        }

        private string DownloadPackage(Package package)
        {
            string id = ((string)package.Id).ToLowerInvariant();
            string version = ((string)package.Version).ToLowerInvariant();
            string hash = WebUtility.UrlEncode((string)package.Hash);

            // Get the blob URL
            var client = StorageAccount.CreateCloudBlobClient();
            var container = client.GetContainerReference("packages");
            var blob = container.GetBlockBlobReference(
                id + "." + version + ".nupkg");

            var localFile = Path.GetTempFileName();
            if (File.Exists(localFile))
            {
                File.Delete(localFile);
            }

            Console.WriteInfoLine(Strings.Package_DownloadingBlob).Wait();
            if (!WhatIf)
            {
                blob.DownloadToFile(localFile, FileMode.CreateNew);
            }

            return localFile;
        }

        private static IEnumerable<string> GetSupportedFrameworks(IPackage nugetPackage)
        {
            return nugetPackage.GetSupportedFrameworks().Select(fn =>
                fn == null ? null : VersionUtility.GetShortFrameworkName(fn)).ToArray();                
        }

        private void PopulateFrameworks(PackageFrameworkReport report, Package package)
        {
            using (var sqlConnection = new SqlConnection(ConnectionString.ConnectionString))
            using (var dbExecutor = new SqlExecutor(sqlConnection))
            {
                sqlConnection.Open();

                // Get all target frameworks in the db for this package
                report.DatabaseFrameworks = new HashSet<string>(dbExecutor.Query<string>(@"
                    SELECT TargetFramework
                    FROM PackageFrameworks
                    WHERE Package_Key = @packageKey",
                    new
                    {
                        packageKey = package.Key
                    })).ToArray();

                var adds = report.PackageFrameworks.Except(report.DatabaseFrameworks).Select(targetFramework =>
                    new PackageFrameworkOperation()
                    {
                        Type = PackageFrameworkOperationType.Add,
                        Framework = targetFramework,
                        Applied = false,
                        Error = "Not Started"
                    });
                var rems = report.DatabaseFrameworks.Except(report.PackageFrameworks).Select(targetFramework =>
                    new PackageFrameworkOperation()
                    {
                        Type = PackageFrameworkOperationType.Remove,
                        Framework = targetFramework,
                        Applied = false,
                        Error = "Not Started"
                    });

                report.Operations = Enumerable.Concat(adds, rems).ToArray();
            }

            return report;
        }

        private async Task LoadDefaultsFromAzure(Datacenter dc)
        {
            bool expired = false;
            try
            {
                if (String.IsNullOrWhiteSpace(DatabaseConnectionString) ||
                    String.IsNullOrWhiteSpace(StorageConnectionString))
                {
                    var config = await LoadServiceConfig(dc, dc.GetService("work"));

                    DatabaseConnectionString = DatabaseConnectionString ??
                        GetValueOrDefault(config, "Sql.Legacy");
                    StorageConnectionString = StorageConnectionString ??
                        GetValueOrDefault(config, "Storage.Legacy");
                }

                if (String.IsNullOrWhiteSpace(DatabaseConnectionString) ||
                    String.IsNullOrWhiteSpace(StorageConnectionString))
                {
                    throw new InvalidOperationException(Strings.Command_MissingEnvironmentArguments);
                }

                await Console.WriteInfoLine(
                    Strings.Command_ConnectionInfo,
                    new SqlConnectionStringBuilder(DatabaseConnectionString).DataSource,
                    CloudStorageAccount.Parse(StorageConnectionString).Credentials.AccountName);
            }
            catch (CloudException ex)
            {
                if (ex.ErrorCode == "AuthenticationFailed")
                {
                    expired = true;
                }
                else
                {
                    throw;
                }
            }

            if (expired)
            {
                await Console.WriteErrorLine(Strings.AzureCommandBase_TokenExpired);
                throw new OperationCanceledException();
            }
        }

        private string GetValueOrDefault(IDictionary<string, string> dict, string key)
        {
            string val;
            if (!dict.TryGetValue(key, out val))
            {
                return null;
            }
            return val;
        }

        private void WriteAuditRecord(AuditRecord auditRecord, string resourceType)
        {
            var entry = new AuditEntry(
                auditRecord,
                AuditActor.GetCurrentMachineActor());

            // Write the blob to the storage account
            var client = StorageAccount.CreateCloudBlobClient();
            var container = client.GetContainerReference("auditing");
            await container.CreateIfNotExistsAsync();
            var blob = container.GetBlockBlobReference(
                resourceType + "/" + auditRecord.GetPath() + "/" + DateTime.UtcNow.ToString("s") + "-" + auditRecord.GetAction().ToLowerInvariant() + ".audit.v1.json");

            if (await blob.ExistsAsync())
            {
                throw new InvalidOperationException(String.Format(
                    CultureInfo.CurrentCulture,
                    Strings.Package_DeleteCommand_AuditBlobExists,
                    blob.Uri.AbsoluteUri));
            }

            byte[] data = Encoding.UTF8.GetBytes(
                JsonFormat.Serialize(entry));
            await blob.UploadFromByteArrayAsync(data, 0, data.Length);
        }

        public class Package
        {
            public string Hash { get; set; }
            public string Id { get; set; }
            public int Key { get; set; }
            public string Version { get; set; }
            public string NormalizedVersion { get; set; }
            public DateTime? Created { get; set; }
        }

        public class PackageFrameworkReport
        {
            public string Id { get; set; }
            public string Version { get; set; }
            public int Key { get; set; }
            public string Hash { get; set; }
            public DateTime Created { get; set; }
            public string[] DatabaseFrameworks { get; set; }
            public string[] PackageFrameworks { get; set; }
            public PackageFrameworkOperation[] Operations { get; set; }
            public string Error { get; set; }

            [JsonConverter(typeof(StringEnumConverter))]
            public PackageReportState State { get; set; }
        }

        public class PackageFrameworkOperation
        {
            [JsonConverter(typeof(StringEnumConverter))]
            public PackageFrameworkOperationType Type { get; set; }
            public string Framework { get; set; }
            public bool Applied { get; set; }
            public string Error { get; set; }
        }

        public enum PackageFrameworkOperationType
        {
            Add,
            Remove
        }

        public enum PackageReportState
        {
            Unresolved,
            Resolved,
            Error
        }
    }
}
