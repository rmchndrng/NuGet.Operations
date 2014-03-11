﻿using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Globalization;
using System.Linq;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using NuGet.Services;
using NuGet.Services.Operations;
using NuGet.Services.Operations.Model;
using NuGet.Services.Operations.Secrets;
using PowerArgs;

namespace NuCmd.Commands.Db
{
    public abstract class DatabaseCommandBase : DatacenterCommandBase
    {
        [ArgRequired]
        [ArgShortcut("db")]
        [ArgDescription("The type of the SQL Database to create the user on")]
        public KnownSqlConnection Database { get; set; }

        [ArgShortcut("au")]
        [ArgDescription("The Admin User Name for the database. Normally not required if the admin is named using the default pattern.")]
        public string AdminUser { get; set; }

        [ArgShortcut("pass")]
        [ArgDescription("The Admin Password. DO NOT SPECIFY THIS WHEN RUNNING INTERACTIVELY!")]
        public string AdminPassword { get; set; }

        protected async Task<SqlConnectionInfo> GetSqlConnectionInfo()
        {
            // Prep the connection string
            EnsureSession();
            var dc = GetDatacenter();

            // Find the server
            var server = dc.FindResource(ResourceTypes.SqlDb, Database.ToString());
            if (server == null)
            {
                throw new InvalidOperationException(String.Format(
                    CultureInfo.CurrentCulture,
                    Strings.Db_DatabaseCommandBase_NoDatabaseInDatacenter,
                    Datacenter.Value,
                    ResourceTypes.SqlDb,
                    Database.ToString()));
            }

            AdminUser = AdminUser ?? Utils.GetAdminUserName(server, dc);

            var connStr = new SqlConnectionStringBuilder(server.Value);
            if (String.IsNullOrEmpty(connStr.InitialCatalog))
            {
                throw new InvalidOperationException(String.Format(
                    CultureInfo.CurrentCulture,
                    Strings.Db_DatabaseCommandBase_ResourceMissingRequiredConnectionStringField,
                    ResourceTypes.SqlDb,
                    server.Name,
                    "InitialCatalog"));
            }
            if (String.IsNullOrEmpty(connStr.DataSource))
            {
                throw new InvalidOperationException(String.Format(
                    CultureInfo.CurrentCulture,
                    Strings.Db_DatabaseCommandBase_ResourceMissingRequiredConnectionStringField,
                    ResourceTypes.SqlDb,
                    server.Name,
                    "DataSource"));
            }
            if (!String.IsNullOrEmpty(connStr.UserID))
            {
                throw new InvalidOperationException(String.Format(
                    CultureInfo.CurrentCulture,
                    Strings.Db_DatabaseCommandBase_ResourceHasUnexpectedConnectionStringField,
                    ResourceTypes.SqlDb,
                    server.Name,
                    "User ID"));
            }
            if (!String.IsNullOrEmpty(connStr.Password))
            {
                throw new InvalidOperationException(String.Format(
                    CultureInfo.CurrentCulture,
                    Strings.Db_DatabaseCommandBase_ResourceHasUnexpectedConnectionStringField,
                    ResourceTypes.SqlDb,
                    server.Name,
                    "Password"));
            }

            if (String.IsNullOrEmpty(AdminPassword))
            {
                // Try getting it from the secret store
                var secrets = await GetEnvironmentSecretStore(Session.CurrentEnvironment);
                if (secrets != null)
                {
                    var secret = await secrets.Read(new SecretName("sqldb." + Utils.GetServerName(connStr.DataSource) + ":admin"), Definition.FullName);
                    if (secret != null)
                    {
                        await Console.WriteInfoLine(Strings.Db_DatabaseCommandBase_UsingSecretStore);
                        AdminPassword = secret.Value;
                    }
                }
            }

            SecureString password;
            if(String.IsNullOrEmpty(AdminPassword)) 
            {
                // Prompt the user for the admin password and put it in a SecureString.
                password = await Console.PromptForPassword(String.Format(
                    CultureInfo.CurrentCulture,
                    Strings.Db_DatabaseCommandBase_EnterAdminPassword,
                    AdminUser));
            }
            else
            {
                // Stuff the password in a secure string and vainly attempt to clear it from unsecured memory.
                password = new SecureString();
                foreach(var chr in AdminPassword) {
                    password.AppendChar(chr);
                }
                AdminPassword = null;
                GC.Collect(); // Futile effort to remove AdminPassword from memory
                password.MakeReadOnly();
            }

            // Create a SQL Credential and return the connection info
            return new SqlConnectionInfo(connStr, new SqlCredential(AdminUser, password));
        }
    }

    public class SqlConnectionInfo
    {
        public SqlConnectionStringBuilder ConnectionString { get; private set; }
        public SqlCredential Credential { get; private set; }

        public SqlConnectionInfo(SqlConnectionStringBuilder connectionString, SqlCredential credential)
        {
            ConnectionString = connectionString;
            Credential = credential;
        }

        public Task<SqlConnection> Connect()
        {
            return ConnectCore(ConnectionString);
        }

        public Task<SqlConnection> Connect(string alternateDatabase)
        {
            var connStr = new SqlConnectionStringBuilder(ConnectionString.ConnectionString)
            {
                InitialCatalog = alternateDatabase
            };
            return ConnectCore(connStr);
        }

        public string GetServerName()
        {
            return Utils.GetServerName(ConnectionString.DataSource);
        }

        private async Task<SqlConnection> ConnectCore(SqlConnectionStringBuilder connStr)
        {
            var conn = new SqlConnection(connStr.ConnectionString, Credential);
            await conn.OpenAsync();
            return conn;
        }
    }
}
