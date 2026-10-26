using Microsoft.Data.SqlClient;
using Xunit;

namespace Wilson.Integration.Tests;

/// <summary>
/// Spec sql-identity-access, "Applications connect to the database with their managed identity": the apps' EIMDB
/// connection string uses Authentication=Active Directory Managed Identity, and the migrator uses Active Directory
/// Default. Microsoft.Data.SqlClient 7 ships those providers in Microsoft.Data.SqlClient.Extensions.Azure, which
/// ARM.Integration.Core references, so every host that reaches eimdb (Automation, gateway, dashboard API, migrator)
/// can open such a connection. No connection is opened here.
/// </summary>
public class SqlEntraAuthenticationTests
{
    // The format of Get-SqlConnectionString in script/deployment/Deploy-Wilson.ps1, with obviously fake names.
    private const string AppConnectionString =
        "Server=tcp:sql-example.database.windows.net,1433;Initial Catalog=sqldb-example;Authentication=Active Directory Managed Identity;Encrypt=True;TrustServerCertificate=False;Connect Timeout=30;";

    [Theory]
    [InlineData(SqlAuthenticationMethod.ActiveDirectoryManagedIdentity)]
    [InlineData(SqlAuthenticationMethod.ActiveDirectoryDefault)]
    public void The_entra_provider_is_available(SqlAuthenticationMethod method)
    {
        Assert.True(SqlAuthenticationProvider.GetProvider(method) is not null,
            $"No SQL authentication provider for {method}. ARM.Integration.Core must reference Microsoft.Data.SqlClient.Extensions.Azure.");
    }

    [Fact]
    public void The_deployed_connection_string_uses_the_managed_identity()
    {
        var builder = new SqlConnectionStringBuilder(AppConnectionString);

        Assert.Equal(SqlAuthenticationMethod.ActiveDirectoryManagedIdentity, builder.Authentication);
        Assert.Equal("sqldb-example", builder.InitialCatalog);
        Assert.True(builder.Encrypt == SqlConnectionEncryptOption.Mandatory, "The connection must be encrypted.");
        Assert.Equal("", builder.Password);
        Assert.Equal("", builder.UserID);
    }
}
