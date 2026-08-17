using System;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using Hollowbound.Simulation;

namespace Hollowbound;

public sealed class Game1 : Game
{
    private const float MinimumTileSize = 8f;
    private readonly GraphicsDeviceManager _graphics;
    private EmergentSimulationWorld _world = new(seed: Random.Shared.Next(1, int.MaxValue), initialPopulation: 2);
    private readonly AnalyticsLogger _analytics = new();
    private SpriteBatch _spriteBatch = null!;
    private Texture2D _pixel = null!;
    private KeyboardState _previousKeyboard;
    private MouseState _previousMouse;
    private float _timeScale = 1f;
    private bool _paused = true;
    private bool _isFullscreen;
    private int _windowedWidth = 1280;
    private int _windowedHeight = 720;
    private int _selectedAgentId = -1;

    public Game1()
    {
        _graphics = new GraphicsDeviceManager(this)
        {
            PreferredBackBufferWidth = 1280,
            PreferredBackBufferHeight = 720,
            SynchronizeWithVerticalRetrace = true,
        };
        Content.RootDirectory = "Content";
        IsMouseVisible = true;
        Window.Title = "Hollowbound";
        _analytics.LogEvent("run_started", _world, _timeScale, _paused);
    }

    protected override void LoadContent()
    {
        _spriteBatch = new SpriteBatch(GraphicsDevice);
        _pixel = new Texture2D(GraphicsDevice, 1, 1);
        _pixel.SetData(new[] { Color.White });
    }

    protected override void Update(GameTime gameTime)
    {
        var keyboard = Keyboard.GetState();
        var mouse = Mouse.GetState();

        if (IsPressed(keyboard, Keys.Escape) || GamePad.GetState(PlayerIndex.One).Buttons.Back == ButtonState.Pressed)
            Exit();

        if (IsPressed(keyboard, Keys.Space))
        {
            _paused = !_paused;
            _world.ClearBacklog();
            _analytics.LogEvent(_paused ? "paused" : "resumed", _world, _timeScale, _paused);
        }

        if (IsPressed(keyboard, Keys.N))
            ResetWorld();

        if (IsPressed(keyboard, Keys.F11) ||
            (IsPressed(keyboard, Keys.Enter) && (keyboard.IsKeyDown(Keys.LeftAlt) || keyboard.IsKeyDown(Keys.RightAlt))))
            ToggleFullscreen();

        if (IsPressed(keyboard, Keys.Tab))
        {
            var previousSpeed = _timeScale;
            _timeScale = _timeScale switch
            {
                1f => 5f,
                5f => 10f,
                10f => 25f,
                25f => 50f,
                50f => 100f,
                100f => 250f,
                250f => 500f,
                _ => 1f,
            };
            _analytics.LogEvent("speed_changed", _world, _timeScale, _paused, $"from={previousSpeed:0};to={_timeScale:0}");
        }

        if (mouse.LeftButton == ButtonState.Pressed && _previousMouse.LeftButton == ButtonState.Released)
            SelectAgent(mouse.Position);

        if (!_paused)
            _world.Advance((float)gameTime.ElapsedGameTime.TotalSeconds, _timeScale);

        _analytics.MaybeWriteSnapshot(_world, _timeScale, _paused);

        _previousKeyboard = keyboard;
        _previousMouse = mouse;
        base.Update(gameTime);
    }

    protected override void Draw(GameTime gameTime)
    {
        GraphicsDevice.Clear(new Color(10, 12, 16));
        _spriteBatch.Begin(samplerState: SamplerState.PointClamp);
        DrawWorld();
        DrawInterface();
        _spriteBatch.End();
        base.Draw(gameTime);
    }

    private void DrawWorld()
    {
        var viewport = GraphicsDevice.Viewport;
        var tileSize = GetTileSize();
        var mapTopLeft = GetMapTopLeft(tileSize);
        var mapBounds = new Rectangle((int)mapTopLeft.X, (int)mapTopLeft.Y, (int)(EmergentSimulationWorld.Width * tileSize), (int)(EmergentSimulationWorld.Height * tileSize));
        DrawRect(mapBounds, new Color(22, 27, 34));

        for (var x = 0; x < EmergentSimulationWorld.Width; x += 4)
            DrawRect(new Rectangle((int)(mapTopLeft.X + x * tileSize), mapBounds.Top, 1, mapBounds.Height), new Color(28, 34, 42));
        for (var y = 0; y < EmergentSimulationWorld.Height; y += 4)
            DrawRect(new Rectangle(mapBounds.Left, (int)(mapTopLeft.Y + y * tileSize), mapBounds.Width, 1), new Color(28, 34, 42));

        foreach (var wall in _world.Map.WallCells)
        {
            var rect = CellRect(wall, tileSize, mapTopLeft);
            DrawRect(rect, new Color(124, 132, 144));
            DrawRect(new Rectangle(rect.Left, rect.Top, rect.Width, Math.Max(1, (int)(tileSize * 0.16f))), new Color(178, 184, 194));
        }

        foreach (var door in _world.Map.DoorCells)
        {
            var rect = CellRect(door, tileSize, mapTopLeft);
            DrawRect(rect, new Color(49, 107, 82));
            DrawRect(new Rectangle(rect.Left + rect.Width / 4, rect.Top + rect.Height / 4, Math.Max(1, rect.Width / 2), Math.Max(1, rect.Height / 2)), new Color(129, 193, 137));
        }

        foreach (var storage in _world.Map.StorageCells)
        {
            var rect = CellRect(storage, tileSize, mapTopLeft);
            DrawRect(rect, new Color(31, 39, 43));
            DrawRect(new Rectangle(rect.Left + rect.Width / 3, rect.Top + rect.Height / 3, Math.Max(1, rect.Width / 3), Math.Max(1, rect.Height / 3)), new Color(90, 72, 45));
        }

        foreach (var node in _world.Food)
        {
            if (node.Amount <= 0)
                continue;
            var rect = CellRect(node.Cell, tileSize, mapTopLeft);
            var size = node.Amount >= 5 ? Math.Max(3, (int)(tileSize * 0.38f)) : Math.Max(2, (int)(tileSize * 0.28f));
            DrawRect(new Rectangle(rect.Center.X - size / 2, rect.Center.Y - size / 2, size, size), new Color(190, 145, 67));
        }

        foreach (var pile in _world.FoodStorage)
        {
            if (pile.Value <= 0)
                continue;
            var rect = CellRect(pile.Key, tileSize, mapTopLeft);
            var size = Math.Max(3, (int)(tileSize * 0.48f));
            DrawRect(new Rectangle(rect.Center.X - size / 2, rect.Center.Y - size / 2, size, size), new Color(111, 79, 44));
            DrawRect(new Rectangle(rect.Center.X - 1, rect.Center.Y - 1, 2, 2), new Color(218, 170, 78));
        }

        foreach (var agent in _world.Agents)
        {
            if (!agent.Alive)
                continue;
            var factionColor = agent.FactionId == 0 ? new Color(84, 161, 174) : new Color(181, 116, 72);
            var rect = CellRect(agent.Cell, tileSize, mapTopLeft);
            var size = Math.Max(6, (int)(tileSize * 0.72f));
            var agentRect = new Rectangle(rect.Center.X - size / 2, rect.Center.Y - size / 2, size, size);
            DrawRect(agentRect, factionColor);
            DrawRect(agentRect, new Color(18, 22, 28), 1);
            var markerSize = Math.Max(2, size / 4);
            var markerX = rect.Center.X + Math.Clamp(agent.Facing.X, -1, 1) * Math.Max(1, size / 4) - markerSize / 2;
            var markerY = rect.Center.Y + Math.Clamp(agent.Facing.Y, -1, 1) * Math.Max(1, size / 4) - markerSize / 2;
            DrawRect(new Rectangle(markerX, markerY, markerSize, markerSize), new Color(226, 211, 154));

            if (agent.Id == _selectedAgentId)
                DrawRect(new Rectangle(agentRect.Left - 3, agentRect.Top - 3, agentRect.Width + 6, agentRect.Height + 6), new Color(228, 220, 163), 2);
        }
    }

    private void DrawInterface()
    {
        var viewport = GraphicsDevice.Viewport;
        DrawRect(new Rectangle(18, 16, Math.Min(viewport.Width - 36, 1100), 82), new Color(8, 10, 13, 225));
        var alive = _world.Agents.Count(agent => agent.Alive);
        var header = $"HOLLOWBOUND  //  tick {_world.Tick:N0}  //  population {alive:N0}  //  seed {_world.Seed}";
        var stats = $"food {_world.FoodStockpile:N0}   wall blocks {_world.Map.WallCells.Count:N0}   passages {_world.WallBlocksRemoved:N0}   births {_world.Births:N0}   deaths {_world.Deaths:N0}   speed x{_timeScale:0}";
        var catchingUp = _world.IsCatchingUp ? "   CATCHING UP" : "";
        var controls = $"[SPACE] {(_paused ? "resume" : "pause")}   [TAB] speed   [N] new seed   [F11] fullscreen   [LMB] inspect";
        DrawText(header, new Vector2(30, 26), new Color(218, 218, 203));
        DrawText(stats + catchingUp, new Vector2(30, 47), new Color(152, 162, 171));
        DrawText(controls, new Vector2(30, 68), new Color(111, 128, 137));

        var selected = _world.Agents.FirstOrDefault(agent => agent.Id == _selectedAgentId);
        if (selected is not null)
        {
            DrawRect(new Rectangle(18, 104, 310, 136), new Color(8, 10, 13, 225));
            DrawText($"AGENT #{selected.Id:0000}", new Vector2(30, 114), new Color(228, 220, 163));
            DrawText($"faction  {selected.FactionId + 1}", new Vector2(30, 136), Color.LightGray);
            DrawText($"action   {selected.Action}", new Vector2(30, 157), Color.LightGray);
            DrawText($"energy   {selected.Energy:0.0}", new Vector2(30, 178), Color.LightGray);
            DrawText($"age      {selected.Age:0.0}s   food {selected.CarriedFood}", new Vector2(30, 199), Color.LightGray);
            DrawText($"learning {selected.FoodKnowledge:0.00}   trips {selected.SuccessfulFoodTrips}", new Vector2(30, 220), Color.LightGray);
        }

        if (_paused)
            DrawText("PAUSED", new Vector2(Math.Max(18, viewport.Width - 110), 24), new Color(228, 220, 163));
    }

    private void SelectAgent(Point mousePosition)
    {
        var tileSize = GetTileSize();
        var mapTopLeft = GetMapTopLeft(tileSize);
        var cell = ScreenToCell(mousePosition, tileSize, mapTopLeft);
        if (!_world.Map.InBounds(cell))
        {
            _selectedAgentId = -1;
            return;
        }

        var nearest = _world.Agents
            .Where(agent => agent.Alive)
            .OrderBy(agent => Math.Abs(agent.Cell.X - cell.X) + Math.Abs(agent.Cell.Y - cell.Y))
            .FirstOrDefault();

        _selectedAgentId = nearest is not null && Math.Abs(nearest.Cell.X - cell.X) <= 1 && Math.Abs(nearest.Cell.Y - cell.Y) <= 1
            ? nearest.Id
            : -1;
    }

    private float GetTileSize()
    {
        var viewport = GraphicsDevice.Viewport;
        var widthScale = (viewport.Width - 40f) / EmergentSimulationWorld.Width;
        var heightScale = (viewport.Height - 40f) / EmergentSimulationWorld.Height;
        return MathF.Max(MinimumTileSize, MathF.Min(widthScale, heightScale));
    }

    private Vector2 GetMapTopLeft(float tileSize)
    {
        var viewport = GraphicsDevice.Viewport;
        var mapSize = new Vector2(EmergentSimulationWorld.Width * tileSize, EmergentSimulationWorld.Height * tileSize);
        return new Vector2(viewport.Width, viewport.Height) / 2f - mapSize / 2f;
    }

    private Rectangle CellRect(Point cell, float tileSize, Vector2 mapTopLeft)
    {
        return new Rectangle((int)(mapTopLeft.X + cell.X * tileSize), (int)(mapTopLeft.Y + cell.Y * tileSize), Math.Max(1, (int)MathF.Ceiling(tileSize)), Math.Max(1, (int)MathF.Ceiling(tileSize)));
    }

    private Point ScreenToCell(Point screen, float tileSize, Vector2 mapTopLeft)
    {
        return new Point((int)MathF.Floor((screen.X - mapTopLeft.X) / tileSize), (int)MathF.Floor((screen.Y - mapTopLeft.Y) / tileSize));
    }

    private void ToggleFullscreen()
    {
        if (!_isFullscreen)
        {
            _windowedWidth = Math.Max(640, Window.ClientBounds.Width);
            _windowedHeight = Math.Max(360, Window.ClientBounds.Height);
            var display = GraphicsDevice.Adapter.CurrentDisplayMode;
            _graphics.PreferredBackBufferWidth = display.Width;
            _graphics.PreferredBackBufferHeight = display.Height;
            _graphics.IsFullScreen = true;
        }
        else
        {
            _graphics.IsFullScreen = false;
            _graphics.PreferredBackBufferWidth = _windowedWidth;
            _graphics.PreferredBackBufferHeight = _windowedHeight;
        }

        _graphics.ApplyChanges();
        _isFullscreen = _graphics.IsFullScreen;
    }

    private void ResetWorld()
    {
        _world = new EmergentSimulationWorld(Random.Shared.Next(1, int.MaxValue), initialPopulation: 2);
        _timeScale = 1f;
        _paused = true;
        _selectedAgentId = -1;
        _analytics.LogEvent("new_world", _world, _timeScale, _paused);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _analytics.Dispose();
        base.Dispose(disposing);
    }

    private void DrawRect(Rectangle rectangle, Color color, int border = 0)
    {
        if (border == 0)
        {
            _spriteBatch.Draw(_pixel, rectangle, color);
            return;
        }

        _spriteBatch.Draw(_pixel, new Rectangle(rectangle.Left, rectangle.Top, rectangle.Width, border), color);
        _spriteBatch.Draw(_pixel, new Rectangle(rectangle.Left, rectangle.Bottom - border, rectangle.Width, border), color);
        _spriteBatch.Draw(_pixel, new Rectangle(rectangle.Left, rectangle.Top, border, rectangle.Height), color);
        _spriteBatch.Draw(_pixel, new Rectangle(rectangle.Right - border, rectangle.Top, border, rectangle.Height), color);
    }

    private void DrawText(string text, Vector2 position, Color color, int scale = 2)
    {
        var cursor = position;
        foreach (var character in text.ToUpperInvariant())
        {
            var glyph = Glyph(character);
            for (var row = 0; row < glyph.Length; row++)
            {
                for (var column = 0; column < glyph[row].Length; column++)
                {
                    if (glyph[row][column] == '#')
                        DrawRect(new Rectangle((int)cursor.X + column * scale, (int)cursor.Y + row * scale, scale, scale), color);
                }
            }
            cursor.X += 6 * scale;
        }
    }

    private static string[] Glyph(char character) => character switch
    {
        'A' => new[] { ".###.", "#...#", "#...#", "#####", "#...#", "#...#", "#...#" },
        'B' => new[] { "####.", "#...#", "#...#", "####.", "#...#", "#...#", "####." },
        'C' => new[] { ".####", "#....", "#....", "#....", "#....", "#....", ".####" },
        'D' => new[] { "####.", "#...#", "#...#", "#...#", "#...#", "#...#", "####." },
        'E' => new[] { "#####", "#....", "#....", "####.", "#....", "#....", "#####" },
        'F' => new[] { "#####", "#....", "#....", "####.", "#....", "#....", "#...." },
        'G' => new[] { ".####", "#....", "#....", "#.###", "#...#", "#...#", ".####" },
        'H' => new[] { "#...#", "#...#", "#...#", "#####", "#...#", "#...#", "#...#" },
        'I' => new[] { "#####", "..#..", "..#..", "..#..", "..#..", "..#..", "#####" },
        'J' => new[] { "..###", "...#.", "...#.", "...#.", "...#.", "#..#.", ".##.." },
        'K' => new[] { "#...#", "#..#.", "#.#..", "##...", "#.#..", "#..#.", "#...#" },
        'L' => new[] { "#....", "#....", "#....", "#....", "#....", "#....", "#####" },
        'M' => new[] { "#...#", "##.##", "#.#.#", "#.#.#", "#...#", "#...#", "#...#" },
        'N' => new[] { "#...#", "##..#", "##..#", "#.#.#", "#..##", "#..##", "#...#" },
        'O' => new[] { ".###.", "#...#", "#...#", "#...#", "#...#", "#...#", ".###." },
        'P' => new[] { "####.", "#...#", "#...#", "####.", "#....", "#....", "#...." },
        'Q' => new[] { ".###.", "#...#", "#...#", "#...#", "#.#.#", "#..#.", ".##.#" },
        'R' => new[] { "####.", "#...#", "#...#", "####.", "#.#..", "#..#.", "#...#" },
        'S' => new[] { ".####", "#....", "#....", ".###.", "....#", "....#", "####." },
        'T' => new[] { "#####", "..#..", "..#..", "..#..", "..#..", "..#..", "..#.." },
        'U' => new[] { "#...#", "#...#", "#...#", "#...#", "#...#", "#...#", ".###." },
        'V' => new[] { "#...#", "#...#", "#...#", "#...#", "#...#", ".#.#.", "..#.." },
        'W' => new[] { "#...#", "#...#", "#...#", "#.#.#", "#.#.#", "##.##", "#...#" },
        'X' => new[] { "#...#", "#...#", ".#.#.", "..#..", ".#.#.", "#...#", "#...#" },
        'Y' => new[] { "#...#", "#...#", ".#.#.", "..#..", "..#..", "..#..", "..#.." },
        'Z' => new[] { "#####", "....#", "...#.", "..#..", ".#...", "#....", "#####" },
        '0' => new[] { ".###.", "#...#", "#..##", "#.#.#", "##..#", "#...#", ".###." },
        '1' => new[] { "..#..", ".##..", "..#..", "..#..", "..#..", "..#..", ".###." },
        '2' => new[] { ".###.", "#...#", "....#", "...#.", "..#..", ".#...", "#####" },
        '3' => new[] { "####.", "....#", "....#", ".###.", "....#", "....#", "####." },
        '4' => new[] { "...#.", "..##.", ".#.#.", "#..#.", "#####", "...#.", "...#." },
        '5' => new[] { "#####", "#....", "#....", "####.", "....#", "....#", "####." },
        '6' => new[] { ".###.", "#....", "#....", "####.", "#...#", "#...#", ".###." },
        '7' => new[] { "#####", "....#", "...#.", "..#..", ".#...", ".#...", ".#..." },
        '8' => new[] { ".###.", "#...#", "#...#", ".###.", "#...#", "#...#", ".###." },
        '9' => new[] { ".###.", "#...#", "#...#", ".####", "....#", "....#", ".###." },
        ':' => new[] { ".....", "..#..", ".....", ".....", "..#..", ".....", "....." },
        '.' => new[] { ".....", ".....", ".....", ".....", ".....", "..#..", "..#.." },
        '#' => new[] { ".#.#.", "#####", ".#.#.", "#####", ".#.#.", ".....", "....." },
        '/' => new[] { "....#", "...#.", "..#..", ".#...", "#....", ".....", "....." },
        '-' => new[] { ".....", ".....", "#####", ".....", ".....", ".....", "....." },
        _ => new[] { ".....", ".....", ".....", ".....", ".....", ".....", "....." },
    };

    private bool IsPressed(KeyboardState current, Keys key) => current.IsKeyDown(key) && !_previousKeyboard.IsKeyDown(key);
}
