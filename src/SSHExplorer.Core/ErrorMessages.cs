using System.Net.Sockets;
using Renci.SshNet.Common;

namespace SSHExplorer.Core;
public static class ErrorMessages
{
    public static string Describe(Exception ex) => ex switch
    {
        HostKeyChangedException => ex.Message,
        SshAgentException => ex.Message,
        SshAuthenticationException when ex.InnerException is SshAgentException agent => agent.Message,
        OperationCanceledException => "Операция отменена.",
        SshAuthenticationException => "Сервер отклонил аутентификацию. Проверьте пользователя, пароль или ключ.",
        SshOperationTimeoutException or TimeoutException => "Истекло время ожидания ответа сервера.",
        SftpPermissionDeniedException or UnauthorizedAccessException => "Недостаточно прав для этой операции.",
        SftpPathNotFoundException or FileNotFoundException or DirectoryNotFoundException => "Файл или папка не найдены.",
        SshConnectionException => "Связь с SSH-сервером потеряна или отклонена.",
        SocketException socket when socket.SocketErrorCode == SocketError.HostNotFound => "DNS-имя сервера не найдено.",
        SocketException socket when socket.SocketErrorCode == SocketError.ConnectionRefused => "Сервер отклонил соединение с указанным портом.",
        SocketException socket when socket.SocketErrorCode == SocketError.TimedOut => "Истекло время ожидания подключения.",
        IOException io when io.Message.Contains("space", StringComparison.OrdinalIgnoreCase) => "Недостаточно свободного места.",
        _ => ex.Message
    };
}
