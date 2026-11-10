using Cyberverse.Plugin.Characters.Data;
using Cyberverse.Server.Commands;
using Cyberverse.Server.Events;
using Cyberverse.Server.Networking;
using Cyberverse.Server.Players;
using Cyberverse.Server.Plugins;
using Cyberverse.Server.Protocol;
using Cyberverse.Server.Threading;
using Cyberverse.Server.Types;
using Microsoft.Extensions.DependencyInjection;
using NLog;

namespace Cyberverse.Plugin.Characters;

/// <summary>
/// Characters in PostgreSQL (CYB-00009). When a player's game joins the world, they get their character list; they
/// create, delete or pick one, and the picked one spawns where it was last saved. Positions are saved every
/// SaveIntervalSeconds and when the player leaves. Registers the server's <see cref="ICharacterProvider"/>: while this
/// plugin is installed, nobody spawns without a character, even when it failed to load.
/// </summary>
public sealed class CharactersPlugin : IPlugin, ICharacterProvider
{
    private readonly Dictionary<uint, Session> _sessions = new();
    private readonly CancellationTokenSource _stopping = new();
    private readonly List<Task> _writes = [];
    private CharacterStore? _store;
    private CharactersConfig? _config;
    private IWorldService? _world;
    private IConnections? _connections;
    private IServerThread? _thread;
    private Logger _logger = LogManager.GetCurrentClassLogger();
    private DateTime _lastSave = DateTime.MinValue;

    public PluginInfo Info { get; } = new("characters", "1.0.0", "player-sync");

    internal Func<DateTime> UtcNow { get; init; } = () => DateTime.UtcNow;

    internal CharacterStore? Store => _store;

    /// <summary>A player whose game joined the world.</summary>
    private sealed class Session(string accountId, string playerName)
    {
        public string AccountId { get; } = accountId;
        public string PlayerName { get; } = playerName;

        /// <summary>A database request for this player is running; further ones are refused until it is done.</summary>
        public bool Busy { get; set; }

        /// <summary>The character being played, null on the select screen.</summary>
        public CharacterSpawn? Playing { get; set; }
    }

    public void ConfigureServices(IServiceCollection services) => services.AddSingleton<ICharacterProvider>(this);

    public void OnLoad(IPluginContext context)
    {
        _logger = context.Logger;
        _world = context.Services.GetRequiredService<IWorldService>();
        _connections = context.Services.GetRequiredService<IConnections>();
        _thread = context.Services.GetRequiredService<IServerThread>();
        var config = context.LoadConfig<CharactersConfig>().WithEnvironment(Environment.GetEnvironmentVariable);
        if (config.MaxCharacters is < 1 or > 16)
        {
            throw new InvalidDataException("maxCharacters must be 1 to 16");
        }

        var store = new CharacterStore(CharactersDbContext.Options(config.Database), UtcNow);
        _logger.Info("Migrating the characters database");
        store.MigrateAsync().GetAwaiter().GetResult();
        _config = config;
        _store = store;

        context.Packets.Register<CharacterCreate>(HandleCreate);
        context.Packets.Register<CharacterSelect>(HandleSelect);
        context.Packets.Register<CharacterDelete>(HandleDelete);
        context.Packets.Register<CharacterAppearance>(HandleAppearance);
        context.Events.Subscribe<PlayerLeft>(e => Leave(e.Player));
        // A connection replaced by a second login of the same account closes without PlayerLeft.
        context.Events.Subscribe<ConnectionClosed>(e => _sessions.Remove(e.ConnectionId));
        context.Events.Subscribe<ServerTick>(e => SaveIfDue(e.UtcNow));
        RegisterCommands(context, store);
        _logger.Info($"Up to {config.MaxCharacters} characters per account, positions saved every {config.SaveIntervalSeconds} s");
    }

    public void OnUnload()
    {
        // The server is stopping: save who is still online.
        if (_store != null && _world != null)
        {
            var positions = _sessions.Keys.Select(Position).OfType<(Guid, float, float, float, float)>().ToList();
            if (positions.Count > 0)
            {
                Track(_store.SavePositionsAsync(positions));
            }
        }

        _stopping.Cancel();
        Task[] pending;
        lock (_writes)
        {
            pending = _writes.ToArray();
        }

        try
        {
            Task.WaitAll(pending, TimeSpan.FromSeconds(10));
        }
        catch (AggregateException e)
        {
            _logger.Error(e, "Saving the characters at shutdown failed");
        }
    }

    public void PlayerJoinedGame(Player player, Vector3 position)
    {
        if (_store == null)
        {
            Fail(player.ConnectionId, "Characters are unavailable: the server's character system failed to start.");
            return;
        }

        if (_sessions.TryGetValue(player.ConnectionId, out var session) && session.Playing is { } playing)
        {
            // A reload: back to where the character was, not where the game loaded.
            var respawn = _world!.TryGetTransform(player.ConnectionId, out var at, out var yaw)
                ? playing with { Position = at, Yaw = yaw }
                : playing;
            _world.Spawn(player.ConnectionId, respawn);
            session.Playing = respawn;
            return;
        }

        session ??= new Session(AccountOf(player), player.Name ?? "");
        _sessions[player.ConnectionId] = session;
        Run(player.ConnectionId, session, async () =>
        {
            var characters = await _store.ListAsync(session.AccountId, _stopping.Token);
            return () => SendList(player.ConnectionId, characters);
        });
    }

    private static string AccountOf(Player player) => player.AccountId ?? "dev:" + player.Name;

    private void HandleCreate(uint connectionId, CharacterCreate packet)
    {
        if (!TryStart(connectionId, out var session, needsSelectScreen: true))
        {
            return;
        }

        if (!CharacterNames.TryNormalize(packet.FirstName, packet.LastName, out var first, out var last, out var error))
        {
            Fail(connectionId, error);
            return;
        }

        Run(connectionId, session, async () =>
        {
            var (result, character) = await _store!.CreateAsync(session.AccountId, first, last, packet.Appearance,
                _config!.MaxCharacters, _stopping.Token);
            if (result != CreateResult.Created)
            {
                var message = result == CreateResult.LimitReached
                    ? $"You already have {_config.MaxCharacters} characters. Delete one first."
                    : $"The name {first} {last} is taken.";
                return () => Fail(connectionId, message);
            }

            _logger.Info($"{session.PlayerName} created {character!.Name} ({character.Id})");
            var characters = await _store.ListAsync(session.AccountId, _stopping.Token);
            return () => SendList(connectionId, characters);
        });
    }

    private void HandleSelect(uint connectionId, CharacterSelect packet)
    {
        if (!TryStart(connectionId, out var session, needsSelectScreen: true))
        {
            return;
        }

        if (!Guid.TryParse(packet.CharacterId, out var id))
        {
            Fail(connectionId, "That character doesn't exist.");
            return;
        }

        Run(connectionId, session, async () =>
        {
            var character = await _store!.FindAsync(session.AccountId, id, _stopping.Token);
            if (character == null)
            {
                return () => Fail(connectionId, "That character doesn't exist.");
            }

            await _store.MarkPlayedAsync(id, _stopping.Token);
            Vector3? position = character.PositionX is { } x && character.PositionY is { } y && character.PositionZ is { } z
                ? new Vector3(x, y, z)
                : null;
            var spawn = new CharacterSpawn(character.Id.ToString(), character.Name, position, character.Yaw,
                character.Appearance);
            return () =>
            {
                // The player may have left meanwhile.
                if (_sessions.ContainsKey(connectionId) && _world!.Spawn(connectionId, spawn))
                {
                    session.Playing = spawn;
                }
            };
        });
    }

    private void HandleDelete(uint connectionId, CharacterDelete packet)
    {
        if (!TryStart(connectionId, out var session, needsSelectScreen: true))
        {
            return;
        }

        if (!Guid.TryParse(packet.CharacterId, out var id))
        {
            Fail(connectionId, "That character doesn't exist.");
            return;
        }

        Run(connectionId, session, async () =>
        {
            if (!await _store!.DeleteAsync(session.AccountId, id, _stopping.Token))
            {
                return () => Fail(connectionId, "That character doesn't exist.");
            }

            _logger.Info($"{session.PlayerName} deleted character {id}");
            var characters = await _store.ListAsync(session.AccountId, _stopping.Token);
            return () => SendList(connectionId, characters);
        });
    }

    private void HandleAppearance(uint connectionId, CharacterAppearance packet)
    {
        if (!_sessions.TryGetValue(connectionId, out var session) || session.Playing is not { } playing ||
            !Guid.TryParse(playing.CharacterId, out var id))
        {
            return;
        }

        session.Playing = playing with { Appearance = packet.Appearance };
        Track(_store!.SaveAppearanceAsync(id, packet.Appearance, _stopping.Token));
    }

    /// <summary>Checks a select-screen request may run now; sends the player why not.</summary>
    private bool TryStart(uint connectionId, out Session session, bool needsSelectScreen)
    {
        if (!_sessions.TryGetValue(connectionId, out session!) || _store == null)
        {
            return false; // the game hasn't joined the world yet
        }

        if (needsSelectScreen && session.Playing != null)
        {
            Fail(connectionId, "You are already playing a character.");
            return false;
        }

        if (session.Busy)
        {
            Fail(connectionId, "Wait until your last request is done.");
            return false;
        }

        return true;
    }

    /// <summary>Runs work off the server thread; the action it returns runs back on the server thread.</summary>
    private void Run(uint connectionId, Session session, Func<Task<Action>> work)
    {
        session.Busy = true;
        Track(Task.Run(async () =>
        {
            Action then;
            try
            {
                then = await work();
            }
            catch (Exception e) when (!_stopping.IsCancellationRequested)
            {
                _logger.Error(e, $"A character request of {session.PlayerName} failed");
                then = () => Fail(connectionId, "The server could not do that. Try again later.");
            }

            _thread!.Post(() =>
            {
                session.Busy = false;
                then();
            });
        }));
    }

    private void Track(Task task)
    {
        lock (_writes)
        {
            _writes.RemoveAll(x => x.IsCompleted);
            _writes.Add(task);
        }

        task.ContinueWith(t => _logger.Error(t.Exception, "Saving a character failed"), TaskContinuationOptions.OnlyOnFaulted);
    }

    private void SendList(uint connectionId, List<Character> characters)
    {
        if (!_sessions.ContainsKey(connectionId))
        {
            return;
        }

        _connections!.Send(connectionId, new CharacterList
        {
            Characters = characters.Select(x => new CharacterInfo
            {
                CharacterId = x.Id.ToString(),
                Name = x.Name,
                LastPlayedUnix = x.LastPlayedAt is { } played ? new DateTimeOffset(played, TimeSpan.Zero).ToUnixTimeSeconds() : 0,
            }).ToArray(),
            MaxCharacters = (byte)_config!.MaxCharacters,
        });
    }

    private void Fail(uint connectionId, string message) =>
        _connections!.Send(connectionId, new CharacterRequestFailed { Message = message });

    private (Guid, float, float, float, float)? Position(uint connectionId)
    {
        if (_sessions.TryGetValue(connectionId, out var session) && session.Playing is { } playing &&
            Guid.TryParse(playing.CharacterId, out var id) && _world!.TryGetTransform(connectionId, out var at, out var yaw))
        {
            return (id, at.X, at.Y, at.Z, yaw);
        }

        return null;
    }

    private void Leave(Player player)
    {
        // PlayerLeft comes before the entity is removed, so the last position is still there.
        if (Position(player.ConnectionId) is { } position)
        {
            Track(_store!.SavePositionsAsync([position]));
        }

        _sessions.Remove(player.ConnectionId);
    }

    private void SaveIfDue(DateTime now)
    {
        if (_store == null || now - _lastSave < TimeSpan.FromSeconds(_config!.SaveIntervalSeconds))
        {
            return;
        }

        _lastSave = now;
        var positions = _sessions.Keys.Select(Position).OfType<(Guid, float, float, float, float)>().ToList();
        if (positions.Count > 0)
        {
            Track(_store.SavePositionsAsync(positions, _stopping.Token));
        }
    }

    private void RegisterCommands(IPluginContext context, CharacterStore store)
    {
        var thread = _thread!;

        void RunAsync(CommandContext ctx, Func<Action<string>, Task> work) => Task.Run(async () =>
        {
            var replies = new List<string>();
            try
            {
                await work(replies.Add);
            }
            catch (Exception e)
            {
                context.Logger.Error(e, $"Command {ctx.Name} failed");
                replies.Add($"Failed: {e.Message}");
            }

            thread.Post(() => replies.ForEach(ctx.Sender.Reply));
        });

        context.Commands.Register(new CommandDefinition("characters", "Lists an account's characters, deleted ones too", ctx =>
        {
            if (ctx.Args.Count == 0)
            {
                ctx.Sender.Reply("Usage: characters <account id | Firstname Lastname>");
                return;
            }

            var query = string.Join(' ', ctx.Args);
            RunAsync(ctx, async reply =>
            {
                var characters = await store.SearchAsync(query);
                if (characters.Count == 0)
                {
                    reply($"No characters for {query}");
                }

                foreach (var c in characters)
                {
                    var deleted = c.DeletedAt is { } at ? $", deleted {at:u}" : "";
                    reply($"{c.Id} {c.Name} (account {c.AccountId}): created {c.CreatedAt:u}, played {c.LastPlayedAt:u}{deleted}");
                }
            });
        }, Usage: "<account id | Firstname Lastname>", Permission: "characters.list"));

        context.Commands.Register(new CommandDefinition("restore-character", "Brings a deleted character back", ctx =>
        {
            if ((ctx.Args.Count != 1 && ctx.Args.Count != 3) || !Guid.TryParse(ctx.Args[0], out var id))
            {
                ctx.Sender.Reply("Usage: restore-character <character id> [Firstname Lastname]");
                return;
            }

            string? first = null, last = null;
            if (ctx.Args.Count == 3)
            {
                if (!CharacterNames.TryNormalize(ctx.Args[1], ctx.Args[2], out var f, out var l, out var error))
                {
                    ctx.Sender.Reply(error);
                    return;
                }

                (first, last) = (f, l);
            }

            RunAsync(ctx, async reply =>
            {
                reply(await store.RestoreAsync(id, first, last) switch
                {
                    RestoreResult.Restored => $"Restored {id}",
                    RestoreResult.NotFound => $"No character {id}",
                    RestoreResult.NotDeleted => $"{id} isn't deleted",
                    _ => "Its name is taken now; give a new one: restore-character <id> Firstname Lastname",
                });
            });
        }, Usage: "<character id> [Firstname Lastname]", Permission: "characters.restore"));
    }
}
