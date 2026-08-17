using System;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using Hollowbound.Simulation;

namespace Hollowbound;

public sealed class Game1 : Game
{
    private const float TileSize = 8f;
    private const float AgentSize = 6f / 8f; // 6 pixels in world units
    private readonly GraphicsDeviceManager _graphics;
    private readonly SimulationWorld _world = new(seed: 47291, initialPopulation: 2);
    private SpriteBatch _spriteBatch = null!;
    private Texture2D _pixel = null!;
    private KeyboardState _previousKeyboard;
    private MouseState _previousMouse;
    private float _timeScale = 1f;
    private bool _paused;
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
            _paused = !_paused;

        if (IsPressed(keyboard, Keys.Tab))
        {
            _timeScale = _timeScale switch
            {
                1f => 5f,
                5f => 25f,
                25f => 100f,
                100f => 500f,
                _ => 1f,
            };
        }

        if (mouse.LeftButton == ButtonState.Pressed && _previousMouse.LeftButton == ButtonState.Released)
            SelectAgent(mouse.Position);

        if (!_paused)
            _world.Advance((float)gameTime.ElapsedGameTime.TotalSeconds, _timeScale);

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
        var center = new Vector2(viewport.Width / 2f, viewport.Height / 2f);
        var mapTopLeft = center - new Vector2(SimulationWorld.Width, SimulationWorld.Height) * TileSize / 2f;
        var mapBounds = new Rectangle((int)mapTopLeft.X, (int)mapTopLeft.Y, (int)(SimulationWorld.Width * TileSize), (int)(SimulationWorld.Height * TileSize));
        DrawRect(mapBounds, new Color(22, 27, 34));

        for (var x = 0; x < SimulationWorld.Width; x += 4)
            DrawRect(new Rectangle((int)(mapTopLeft.X + x * TileSize), mapBounds.Top, 1, mapBounds.Height), new Color(28, 34, 42));
        for (var y = 0; y < SimulationWorld.Height; y += 4)
            DrawRect(new Rectangle(mapBounds.Left, (int)(mapTopLeft.Y + y * TileSize), mapBounds.Width, 1), new Color(28, 34, 42));

        foreach (var node in _world.Food)
        {
            if (node.Amount <= 0)
                continue;
            var size = node.Amount >= 5 ? 4 : 3;
            DrawWorldRect(node.Position, new Vector2(size / TileSize, size / TileSize), new Color(161, 125, 61));
        }

        DrawShelter();

        foreach (var agent in _world.Agents)
        {
            if (!agent.Alive)
                continue;
            var factionColor = agent.FactionId == 0 ? new Color(84, 161, 174) : new Color(181, 116, 72);
            var borderColor = new Color(20, 24, 30);
            DrawWorldRect(agent.Position, new Vector2(AgentSize, AgentSize), factionColor);
            DrawWorldRectBorder(agent.Position, new Vector2(AgentSize, AgentSize), borderColor, 1);

            if (agent.Id == _selectedAgentId)
            {
                var screen = WorldToScreen(agent.Position);
                var rect = new Rectangle((int)screen.X - 4, (int)screen.Y - 4, (int)(AgentSize * TileSize) + 8, (int)(AgentSize * TileSize) + 8);
                DrawRect(rect, new Color(228, 220, 163), 2);
            }
        }
    }

    private void DrawShelter()
    {
        var shelter = _world.Shelter;
        var wallColor = new Color(140, 145, 155);
        var wallHighlight = new Color(180, 185, 195);
        const float wall = 0.75f;
        var top = new Vector2(shelter.X, shelter.Y);
        var left = new Vector2(shelter.X, shelter.Y);
        var right = new Vector2(shelter.Right - wall, shelter.Y);
        var bottom = new Vector2(shelter.X, shelter.Bottom - wall);

        DrawWorldRect(top, new Vector2(shelter.Width, wall), wallColor);
        DrawWorldRect(left, new Vector2(wall, shelter.Height), wallColor);
        DrawWorldRect(right, new Vector2(wall, shelter.Height), wallColor);

        DrawWorldRect(bottom, new Vector2(6f, wall), wallColor);
        DrawWorldRect(new Vector2(shelter.Right - 6f, shelter.Bottom - wall), new Vector2(6f, wall), wallColor);

        const float highlight = 0.15f;
        DrawWorldRect(new Vector2(shelter.X, shelter.Y + 0.1f), new Vector2(shelter.Width, highlight), wallHighlight);
        DrawWorldRect(new Vector2(shelter.X + 0.1f, shelter.Y), new Vector2(highlight, shelter.Height), wallHighlight);
        DrawWorldRect(new Vector2(shelter.Right - wall - 0.1f, shelter.Y), new Vector2(highlight, shelter.Height), wallHighlight);

        var storedFood = Math.Min(16, _world.FoodStockpile);
        for (var i = 0; i < storedFood; i++)
        {
            var storagePosition = new Vector2(shelter.X + 2 + i % 4, shelter.Y + 2 + i / 4);
            DrawWorldRect(storagePosition, new Vector2(0.45f, 0.45f), new Color(190, 145, 67));
        }
    }

    private void DrawInterface()
    {
        DrawRect(new Rectangle(18, 16, 760, 76), new Color(8, 10, 13, 225));
        var alive = _world.Agents.Count(agent => agent.Alive);
        var header = $"HOLLOWBOUND  //  tick {_world.Tick:N0}  //  population {alive:N0}  //  seed {_world.Seed}";
        var stats = $"food {_world.FoodStockpile:N0}   births {_world.Births:N0}   deaths {_world.Deaths:N0}   speed x{_timeScale:0}";
        var catchingUp = _world.IsCatchingUp ? "   CATCHING UP" : "";
        var controls = $"[SPACE] {(_paused ? "resume" : "pause")}   [TAB] speed   [LMB] inspect agent";
        DrawText(header, new Vector2(30, 26), new Color(218, 218, 203));
        DrawText(stats + catchingUp, new Vector2(30, 47), new Color(152, 162, 171));
        DrawText(controls, new Vector2(30, 68), new Color(111, 128, 137));

        var selected = _world.Agents.FirstOrDefault(agent => agent.Id == _selectedAgentId);
        if (selected is not null)
        {
            DrawRect(new Rectangle(18, 104, 280, 112), new Color(8, 10, 13, 225));
            DrawText($"AGENT #{selected.Id:0000}", new Vector2(30, 114), new Color(228, 220, 163));
            DrawText($"faction  {selected.FactionId + 1}", new Vector2(30, 136), Color.LightGray);
            DrawText($"action   {selected.Action}", new Vector2(30, 157), Color.LightGray);
            DrawText($"energy   {selected.Energy:0.0}", new Vector2(30, 178), Color.LightGray);
            DrawText($"age      {selected.Age:0.0}s   food {selected.CarriedFood}", new Vector2(30, 199), Color.LightGray);
        }

        if (_paused)
            DrawText("PAUSED", new Vector2(1160, 24), new Color(228, 220, 163));
    }

    private void SelectAgent(Point mousePosition)
    {
        var viewport = GraphicsDevice.Viewport;
        var center = new Vector2(viewport.Width / 2f, viewport.Height / 2f);
        var mapTopLeft = center - new Vector2(SimulationWorld.Width, SimulationWorld.Height) * TileSize / 2f;
        var worldPosition = (mousePosition.ToVector2() - mapTopLeft) / TileSize;
        var nearest = _world.Agents
            .Where(agent => agent.Alive)
            .OrderBy(agent => Vector2.DistanceSquared(agent.Position, worldPosition))
            .FirstOrDefault();

        _selectedAgentId = nearest is not null && Vector2.Distance(nearest.Position, worldPosition) <= 1.5f ? nearest.Id : -1;
    }

    private Vector2 WorldToScreen(Vector2 worldPosition)
    {
        var viewport = GraphicsDevice.Viewport;
        var center = new Vector2(viewport.Width / 2f, viewport.Height / 2f);
        return center + (worldPosition - new Vector2(SimulationWorld.Width, SimulationWorld.Height) / 2f) * TileSize;
    }

    private void DrawWorldRect(Vector2 position, Vector2 size, Color color)
    {
        var topLeft = WorldToScreen(position);
        DrawRect(new Rectangle((int)topLeft.X, (int)topLeft.Y, Math.Max(1, (int)(size.X * TileSize)), Math.Max(1, (int)(size.Y * TileSize))), color);
    }

    private void DrawWorldRectBorder(Vector2 position, Vector2 size, Color color, int border)
    {
        var topLeft = WorldToScreen(position);
        var rect = new Rectangle((int)topLeft.X, (int)topLeft.Y, Math.Max(1, (int)(size.X * TileSize)), Math.Max(1, (int)(size.Y * TileSize)));
        DrawRect(rect, color, border);
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
