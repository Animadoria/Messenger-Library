using System.Text.Json;

namespace MockerBot;

public class BotConfiguration
{
    private static readonly string CONFIG_PATH = $"{Directory.GetCurrentDirectory()}/bot.json";

    public string Email { get; set; } = "CHANGEME";

    public string Password { get; set; } = "CHANGEME";

    public static BotConfiguration? Parse()
    {
        if (!File.Exists(CONFIG_PATH))
        {
            Console.WriteLine("The configuration file does not exist! Creating one right now :)");

            File.WriteAllText(CONFIG_PATH, JsonSerializer.Serialize(new BotConfiguration()));
            return null;
        }

        try
        {
            string json = File.ReadAllText(CONFIG_PATH);
            return JsonSerializer.Deserialize<BotConfiguration>(json);
        }
        catch (JsonException e)
        {
            Console.WriteLine($"Couldn't read JSON! Are you sure it's valid?\n{e}");
            return null;
        }
    }
}