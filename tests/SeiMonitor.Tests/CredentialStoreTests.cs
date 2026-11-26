using System.Runtime.Versioning;
using SeiMonitor.Core;
using Xunit;

namespace SeiMonitor.Tests;

[SupportedOSPlatform("windows")]
public class CredentialStoreTests : IDisposable
{
    private readonly List<string> _createdTargets = new();

    private string CreateUniqueTarget()
    {
        var target = $"SEI Monitor:Test:{Guid.NewGuid():N}";
        _createdTargets.Add(target);
        return target;
    }

    public void Dispose()
    {
        // Cleanup garantido de todos os targets gerados na execução
        foreach (var target in _createdTargets)
        {
            try
            {
                CredentialStore.DeleteCredential(target);
            }
            catch
            {
                // Silencia falha de cleanup
            }
        }
    }

    [Fact]
    public void SaveAndLoadCredential_RoundTrip_Succeeds()
    {
        var target = CreateUniqueTarget();
        const string user = "usuario.teste";
        const string pass = "SenhaComplexa@2026!#";

        var saved = CredentialStore.SaveCredential(target, user, pass);
        Assert.True(saved);

        var (loadedUser, loadedPass) = CredentialStore.LoadCredential(target);
        Assert.Equal(user, loadedUser);
        Assert.Equal(pass, loadedPass);
    }

    [Fact]
    public void LoadCredential_NonExistent_ReturnsNullTuple()
    {
        var target = CreateUniqueTarget();

        var (user, pass) = CredentialStore.LoadCredential(target);
        Assert.Null(user);
        Assert.Null(pass);
    }

    [Fact]
    public void SaveCredential_Overwrite_UpdatesStoredValues()
    {
        var target = CreateUniqueTarget();

        CredentialStore.SaveCredential(target, "user1", "pass1");
        CredentialStore.SaveCredential(target, "user2", "pass2");

        var (user, pass) = CredentialStore.LoadCredential(target);
        Assert.Equal("user2", user);
        Assert.Equal("pass2", pass);
    }

    [Fact]
    public void DeleteCredential_Existing_RemovesCredential()
    {
        var target = CreateUniqueTarget();
        CredentialStore.SaveCredential(target, "user", "pass");

        var deleted = CredentialStore.DeleteCredential(target);
        Assert.True(deleted);

        var (user, pass) = CredentialStore.LoadCredential(target);
        Assert.Null(user);
        Assert.Null(pass);
    }

    [Fact]
    public void DeleteCredential_NonExistent_IsIdempotent()
    {
        var target = CreateUniqueTarget();

        var deleted = CredentialStore.DeleteCredential(target);
        Assert.True(deleted);
    }

    [Fact]
    public void SaveAndLoad_UnicodeAndSpecialChars_PreservesExactString()
    {
        var target = CreateUniqueTarget();
        const string user = "jõao.açúcar_🎯";
        const string pass = "P@sswórd_€_ç_!#$%^&*()_+|~1234567890";

        var saved = CredentialStore.SaveCredential(target, user, pass);
        Assert.True(saved);

        var (loadedUser, loadedPass) = CredentialStore.LoadCredential(target);
        Assert.Equal(user, loadedUser);
        Assert.Equal(pass, loadedPass);
    }

    [Fact]
    public void SaveAndLoad_EmptyPassword_Succeeds()
    {
        var target = CreateUniqueTarget();
        const string user = "user.no.pass";
        const string pass = "";

        var saved = CredentialStore.SaveCredential(target, user, pass);
        Assert.True(saved);

        var (loadedUser, loadedPass) = CredentialStore.LoadCredential(target);
        Assert.Equal(user, loadedUser);
        Assert.Equal(string.Empty, loadedPass);
    }

    [Fact]
    public void SaveAndLoad_LongJwtToken_Succeeds()
    {
        var target = CreateUniqueTarget();
        const string user = "service_account";
        var pass = new string('A', 256); // CRED_MAX_CREDENTIAL_BLOB_SIZE = 512 bytes (256 caracteres UTF-16LE)

        var saved = CredentialStore.SaveCredential(target, user, pass);
        Assert.True(saved);

        var (loadedUser, loadedPass) = CredentialStore.LoadCredential(target);
        Assert.Equal(user, loadedUser);
        Assert.Equal(pass, loadedPass);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void SaveCredential_InvalidTarget_ThrowsArgumentException(string? invalidTarget)
    {
        Assert.ThrowsAny<ArgumentException>(() =>
            CredentialStore.SaveCredential(invalidTarget!, "user", "pass"));
    }

    [Fact]
    public void SaveCredential_NullUsernameOrPassword_ThrowsArgumentNullException()
    {
        var target = CreateUniqueTarget();
        Assert.Throws<ArgumentNullException>(() => CredentialStore.SaveCredential(target, null!, "pass"));
        Assert.Throws<ArgumentNullException>(() => CredentialStore.SaveCredential(target, "user", null!));
    }

    [Fact]
    public void BuildTarget_ReturnsCorrectTargetFormat()
    {
        Assert.Equal("SEI Monitor", CredentialStore.BuildTarget(null));
        Assert.Equal("SEI Monitor", CredentialStore.BuildTarget(""));
        Assert.Equal("SEI Monitor:joao", CredentialStore.BuildTarget("joao"));
    }
}
