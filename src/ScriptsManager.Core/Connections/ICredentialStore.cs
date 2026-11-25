namespace ScriptsManager.Core.Connections;

/// <summary>Хранилище паролей подключений (в приложении — Windows Credential Manager).</summary>
public interface ICredentialStore
{
    string? GetPassword(SavedConnection connection);
    void SavePassword(SavedConnection connection, string password);
    void DeletePassword(SavedConnection connection);
}
