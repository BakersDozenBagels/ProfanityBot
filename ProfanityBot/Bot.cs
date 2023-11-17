using Discord.WebSocket;
using Discord;
using Discord.Net;
using Newtonsoft.Json;
using System.Threading.Channels;
using System.Text.RegularExpressions;
using System.Text;
using Npgsql;
using System.Data.Common;
using Discord.Rest;

namespace ProfanityBot;

internal class Bot
{
    private record struct DBKey(ulong Guild, ulong User);

    public Bot()
    {
        _client = new DiscordSocketClient(new DiscordSocketConfig() { GatewayIntents = GatewayIntents.Guilds | GatewayIntents.GuildMessages | GatewayIntents.MessageContent });
    }

    private readonly DiscordSocketClient _client;
    private readonly Dictionary<DBKey, (SocketTextChannel? channel, float? rate)> _responses = new();

    public async Task Start()
    {
        _client.Log += Log;

        _client.Ready += CreateSlashCommands;
        _client.Ready += QueryDB;
        _client.SlashCommandExecuted += SlashCommandHandler;
        _client.MessageReceived += OnMessageReceivedAsync;

        var token = Environment.GetEnvironmentVariable("DISCORD_BOT_TOKEN");
        await _client.LoginAsync(TokenType.Bot, token);
        await _client.StartAsync();

        // Block this task until the program is closed.
        await Task.Delay(-1);
    }

    private async Task QueryDB()
    {
        var url = Environment.GetEnvironmentVariable("DATABASE_URL");
        if (url is null)
        {
            Log("No database URL provided. Please set the DATABASE_URL environment variable.");
            return;
        }
        await using var dataSource = NpgsqlDataSource.Create(ParseString(url));
        await using var cmd1 = dataSource.CreateCommand("CREATE TABLE IF NOT EXISTS data (guild BIGINT NOT NULL, guild_user BIGINT NOT NULL, channel BIGINT, rate REAL, UNIQUE (guild, guild_user));");
        await cmd1.ExecuteNonQueryAsync();
        await using var cmd = dataSource.CreateCommand("SELECT * FROM data");
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            Log($"Loading previous settings: guild: {(ulong)reader.GetInt64(0)}; auth: {(ulong)reader.GetInt64(1)}");
            _responses.Add(
                new(
                    (ulong)reader.GetInt64(0),
                    (ulong)reader.GetInt64(1)
                ),
                (
                    reader.IsDBNull(2) ? null : _client.GetGuild((ulong)reader.GetInt64(0)).GetTextChannel((ulong)reader.GetInt64(2)),
                    reader.IsDBNull(3) ? null : reader.GetFloat(3)
                )
            );
        }
    }

    private static Regex _connectionRegex = new(@"postgres://([^:]+):([^@]+)@([a-z-.]+:\d{1,4})", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.ECMAScript);
    private static string ParseString(string url)
    {
        var m = _connectionRegex.Match(url);
        return $"Host={m.Groups[3].Value}; Username={m.Groups[1].Value}; Password={m.Groups[2].Value}; SSL Mode=Disable;";
    }

    private static async Task PostDB(IGuild Guild, ulong User, SocketTextChannel? channel, float? rate, bool update)
    {
        var url = Environment.GetEnvironmentVariable("DATABASE_URL");
        if (url is null)
            return;
        if (update)
            await DeleteDB(Guild, User);
        await using var dataSource = NpgsqlDataSource.Create(ParseString(url));
        await using var conn = await dataSource.OpenConnectionAsync();
        using var comm = new NpgsqlCommand($"INSERT INTO data (guild, guild_user{(channel is null ? "" : ", channel")}{(rate is null ? "" : ", rate")}) VALUES ({(long)Guild.Id}, {(long)User.Id}{(channel is null ? "" : $", {channel.Id}")}{(rate is null ? "" : $", {rate}")})", conn);
        await comm.ExecuteNonQueryAsync();
        Log($"Posting settings: guild: {Guild.Id}; auth: {User}");
    }

    private static async Task DeleteDB(IGuild Guild, ulong? User = null)
    {
        var url = Environment.GetEnvironmentVariable("DATABASE_URL");
        if (url is null)
            return;
        await using var dataSource = NpgsqlDataSource.Create(ParseString(url));
        await using var conn = dataSource.CreateConnection();
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("DELETE FROM data WHERE guild = $1" + (User is null ? "" : $" AND guild_user = {(long)User}"), conn)
        {
            Parameters =
            {
                new() { Value = (long?)Guild?.Id }
            }
        };
        await cmd.ExecuteNonQueryAsync();
        await conn.CloseAsync();
    }

    private async Task OnMessageReceivedAsync(SocketMessage message)
    {
        var guild = (message.Channel as SocketGuildChannel)?.Guild;
        if (guild is null)
        {
            //Log($"Found a message with no guild: {message.Id}");
            return;
        }

        if (!_responses.ContainsKey(new(guild.Id, message!.Author.Id)))
            return;
        Log($"Got message: guild: {guild.Id}; auth: {message!.Author.Id}");

        var to = _responses[new(guild.Id, message.Author.Id)].channel ?? (SocketTextChannel)message.Channel!;

        if (message.Content == "" && message.Embeds.Count == 0)
        {
            await to.SendMessageAsync("I found a message to reply to, but I can't see what it is. Are you sure I have permission to view message contents?");
            return;
        }

        await to.SendMessageAsync(Profanitify(message.Content, _responses[new(guild.Id, message.Author.Id)].rate), embeds: message.Embeds.Select(e => ProcessEmbed(e, _responses[new(guild.Id, message.Author.Id)].rate)).ToArray());
    }

    private static Embed ProcessEmbed(IEmbed embed, float? rate)
    {
        rate ??= 0.4f;

        var ret = new EmbedBuilder();
        if (embed.Author is not null)
            ret = ret.WithAuthor(new EmbedAuthorBuilder().WithName(Profanitify(embed.Author.Value.Name, rate)).WithIconUrl(embed.Author.Value.IconUrl).WithUrl(embed.Author.Value.Url));
        if (embed.Color is not null)
            ret = ret.WithColor(embed.Color.Value);
        if (embed.Description is not null)
            ret = ret.WithDescription(Profanitify(embed.Description, rate));
        if (embed.Footer is not null)
            ret = ret.WithFooter(new EmbedFooterBuilder().WithText(Profanitify(embed.Footer.Value.Text, rate)).WithIconUrl(embed.Footer.Value.IconUrl));
        if (embed.Image is not null)
            ret = ret.WithImageUrl(embed.Image.Value.Url);
        if (embed.Thumbnail is not null)
            ret = ret.WithThumbnailUrl(embed.Thumbnail.Value.Url);
        if (embed.Timestamp is not null)
            ret = ret.WithTimestamp(embed.Timestamp.Value);
        if (embed.Title is not null)
            ret = ret.WithTitle(Profanitify(embed.Title, rate));
        if (embed.Url is not null)
            ret = ret.WithUrl(embed.Url);

        return ret.Build();
    }

    // Heavily trimmed-down version of https://en.wiktionary.org/wiki/Category:English_vulgarities
    private static readonly string[] _profanity = new[]
    {
#region Profanity
        "ass",
        "assclown",
        "asshat",
        "asshole",
        "badass",
        "balls",
        "bastard",
        "batshit",
        "bazonga",
        "bellend",
        "bitch",
        "bloody",
        "bollocks",
        "booty",
        "brotherfucker",
        "bugger",
        "bulge",
        "bullshit",
        "bussy",
        "butt",
        "cacky",
        "choad",
        "clit",
        "cock",
        "crap",
        "cum",
        "damn",
        "dick",
        "dipshit",
        "douche",
        "dumbass",
        "fanny",
        "fap",
        "fuck",
        "frick",
        "goddamn",
        "hell",
        "jackoff",
        "jizz",
        "motherfucker",
        "musk",
        "nutsack",
        "penis",
        "piss",
        "poop",
        "prick",
        "pussy",
        "rump",
        "shart",
        "shit",
        "shite",
        "sisterfucker",
        "slut",
        "sperm",
        "thrussy",
        "tits",
        "turd",
        "twat",
        "vagina",
        "wanker",
        "whore"
#endregion
    };
    private static readonly Regex _wordRegex = new(@"(?<=^|\W)(\w)\w*(?=\W|$)", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.ECMAScript);
    private static readonly Random _random = new();
    private static string Profanitify(string content, float? rate)
    {
        if (rate > 1f || rate < 0f)
            rate = null;
        rate ??= 0.4f;
        var words = _wordRegex
            .Matches(content)
            .Where(m => _profanity.Any(sw => sw.StartsWith(m.Groups[1].Value, true, null)))
            .OrderBy(_ => _random.Next())
            .ToArray();

        List<(int start, int length, string replacement)> replacements = new();

        for (int i = 0; i < words.Length * rate; i++)
        {
            var allowed = _profanity.Where(w => w.StartsWith(words[i].Groups[1].Value, true, null)).ToList();
            replacements.Add((words[i].Index, words[i].Length, allowed[_random.Next(allowed.Count)]));
        }

        StringBuilder output = new();

        for (int i = 0; i < content.Length; i++)
        {
            var place = replacements.FirstOrDefault(r => r.start <= i && r.start + r.length > i);
            if (place == default)
                output.Append(content[i]);
            else
            {
                output.Append(place.replacement);
                i += place.length - 1;
            }
        }

        return output.ToString();
    }

    private async Task SlashCommandHandler(SocketSlashCommand command)
    {
        switch (command.Data.Name)
        {
            case "reply-to-user":
                await HandleRegisterCommand(command);
                break;
            case "clear-settings":
                await HandleDeregisterCommand(command);
                break;
        }
    }

    private async Task HandleRegisterCommand(SocketSlashCommand command)
    {
        var guild = _client.GetGuild(command.GuildId!.Value);
        var user = ParseUser((string)command.Data.Options.First().Value);
        var channel = (IChannel?)command.Data.Options.FirstOrDefault(op => op.Name == "channel")?.Value;
        var rate = (double?)command.Data.Options.FirstOrDefault(op => op.Name == "rate")?.Value;
        float? trueRate = null;
        if (channel is not null && channel is not SocketTextChannel)
        {
            await command.RespondAsync($"That channel (<#{channel.Id}>) is not a text channel.", ephemeral: true);
            return;
        }
        if (rate is not null)
        {
            if (rate < 0 || rate > 100)
            {
                await command.RespondAsync($"That rate ({rate}) is invalid. I expected a number between 0 and 100.", ephemeral: true);
                return;
            }
            trueRate = rate is null ? null : (float)rate * 0.01f;
        }
        if (_responses.ContainsKey(new(guild.Id, user)))
        {
            _responses[new(guild.Id, user)] = ((SocketTextChannel?)channel, trueRate);
            await PostDB(guild, user, (SocketTextChannel?)channel, trueRate, true);
        }
        else
        {
            _responses.Add(new(guild.Id, user), ((SocketTextChannel?)channel, trueRate));
            await PostDB(guild, user, (SocketTextChannel?)channel, trueRate, false);
        }

        await command.RespondAsync($"User <@{user}> will be replied to {(channel == null ? "where they send their messages" : $"in <#{channel.Id}>")}.", ephemeral: true);
    }

    private static readonly Regex _userRegex = new(@"(<@)?(\d+)(?(1)>|)", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.ECMAScript);
    private static ulong ParseUser(string value) => ulong.Parse(_userRegex.Match(value).Groups[2].Value);

    private async Task HandleDeregisterCommand(SocketSlashCommand command)
    {
        var guild = _client.GetGuild(command.GuildId!.Value);

        var count = 0;
        await DeleteDB(guild);
        foreach (var resp in _responses.Keys.Where(t => t.Guild == guild.Id))
        {
            _responses.Remove(resp);
            count++;
        }
        await command.RespondAsync($"No users will be replied to. Stopped listening to {count} user{(count == 1 ? "'s" : "s'")} messages.", ephemeral: true);
    }

    private async Task CreateSlashCommands()
    {
#if DEBUG
        var server = _client.GetGuild(984800593103163453uL);
#endif

        var command1 = new SlashCommandBuilder()
            .WithName("reply-to-user")
            .WithDescription("Sets the bot to reply to a given user.")
            .AddOption("user", ApplicationCommandOptionType.String, "The users to reply to", isRequired: true)
            .AddOption("channel", ApplicationCommandOptionType.Channel, "The channel to reply in", isRequired: false)
            .AddOption("rate", ApplicationCommandOptionType.Number, "The percentage of words to replace", isRequired: false)
            .WithDMPermission(false)
            .WithDefaultMemberPermissions(GuildPermission.ManageGuild);
        var command2 = new SlashCommandBuilder()
            .WithName("clear-settings")
            .WithDescription("Sets the bot to reply to nobody.")
            .WithDMPermission(false)
            .WithDefaultMemberPermissions(GuildPermission.ManageGuild);

        try
        {
#if DEBUG
            await server.CreateApplicationCommandAsync(command1.Build());
            await server.CreateApplicationCommandAsync(command2.Build());
#endif
            await _client.CreateGlobalApplicationCommandAsync(command1.Build());
            await _client.CreateGlobalApplicationCommandAsync(command2.Build());
        }
        catch (HttpException exception)
        {
            Log("Error creating slash commands:");
            var json = JsonConvert.SerializeObject(exception.Errors, Formatting.Indented);
            Log(json);
        }
        Log("Slash commands registered.");
    }

    private static Task Log(LogMessage msg)
    {
        Log(msg.ToString());
        return Task.CompletedTask;
    }
    private static void Log(string msg)
    {
        Console.WriteLine(msg.ToString());
    }
}