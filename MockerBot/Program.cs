using System.Drawing;
using System.Net;
using System.Text;
using MessengerLibrary;

namespace MockerBot;

internal static class Program
{
    private static BotConfiguration configuration = null!;
    private static MessengerClient messenger = null!;

    public static async Task Main(string[] args)
    {
        BotConfiguration? cfg = BotConfiguration.Parse();
        if (cfg == null)
        {
            Environment.Exit(0);
            return;
        }

        configuration = cfg;

        messenger = new MessengerClient(new Credentials(configuration.Email, configuration.Password));

        // Initialize events.
        messenger.LoggedIn += MessengerOnLoggedIn;
        try
        {
            await messenger.LoginAsync();
        }
        catch (AuthenticationErrorException ex)
        {
            Console.WriteLine($"Couldn't authenticate - bad credentials!\n{ex}");
            return;
        }

        messenger.LoggedOut += MessengerOnLoggedOut;
        messenger.IMSessionCreated += MessengerOnIMSessionCreated;
        messenger.InvitedToIMSession += MessengerOnInvitedToIMSession;
        // Don't stop execution
        await Task.Delay(-1);
    }

    private static void MessengerOnMessageReceived(object? sender, MessageEventArgs e)
    {
        if (sender is not IMSession session)
        {
            return;
        }
        Console.WriteLine($"[Message] {e.SenderLoginName} ({e.Message.ContentType})");
        if (!e.Message.ContentType.StartsWith("text/plain"))
        {
            // We only use plain text messages.
            return;
        }

        // Do it in async not to block other messages, just in case.
        _ = Task.Run(async () =>
        {
            string body = Encoding.UTF8.GetString(e.Message.Body);
            Console.WriteLine(body);

            // Let's mock this message!
            string mocked = new(body.ToLower().Select(x => Random.Shared.Next(2) == 0 ? x : char.ToUpper(x)).ToArray());

            // Send it
            var msg = new Message();
            new MessageFormatter()
            {
                Font = "Comic Sans MS",
                Color = Color.DeepPink
            }.ApplyFormat(msg);

            msg.Body = Encoding.UTF8.GetBytes(mocked);

            await session.SendMessageAsync(msg);
        });
    }

    private static void MessengerOnInvitedToIMSession(object? sender, InvitationEventArgs e)
    {
        Console.WriteLine($"Invited to an IM session by {e.Invitation.InvitingUser.LoginName}, joining!");

        Task.Run(async () =>
        {
            IMSession? session = await messenger.AcceptInvitationAsync(e.Invitation);
            session.MessageReceived += MessengerOnMessageReceived;
            session.UserJoined += SessionOnUserJoined;
            session.UserParted += SessionOnUserParted;
        });
    }

    private static void SessionOnUserParted(object? sender, UserEventArgs e)
    {
        Console.WriteLine($"{e.User} left!");
    }

    private static void SessionOnUserJoined(object? sender, UserEventArgs e)
    {
        Console.WriteLine($"{e.User} joined!");
    }

    private static void MessengerOnIMSessionCreated(object? sender, IMSessionEventArgs e)
    {
        Console.WriteLine("IM session was created, with users " +
                          string.Join(", ", e.IMSession.Users.Select(x => x.LoginName)));
    }

    private static void MessengerOnLoggedOut(object? sender, LoggedOutEventArgs e)
    {
        Console.WriteLine($"Logged out! Stopping. Reason: {e.Reason}");
        Environment.Exit(0);
    }

    private static void MessengerOnLoggedIn(object? sender, EventArgs e)
    {
        Console.WriteLine("Successfully logged in!");

        // Change names
        Task.Run(async () =>
        {
            await messenger.LocalUser.ChangeNicknameAsync("The Mocker");
        });
    }
}