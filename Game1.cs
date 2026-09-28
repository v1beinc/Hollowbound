using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using Hollowbound.Simulation;

namespace Hollowbound;

public sealed partial class Game1 : Game
{
    private readonly GraphicsDeviceManager _graphics;
    private EmergentSimulationWorld _world = new(seed: Random.Shared.Next(1, int.MaxValue), initialPopulation: 2);
    private readonly AnalyticsLogger _analytics = new();
    private SpriteBatch _spriteBatch = null!;
    private Texture2D _pixel = null!;
    private RasterizerState _scissorRasterizerState = null!;
    private KeyboardState _previousKeyboard;
    private MouseState _previousMouse;
    private float _timeScale = 1f;
    private bool _paused = true;
    private bool _isFullscreen;
    private int _windowedWidth = 1280;
    private int _windowedHeight = 720;
    private bool _windowModeTransition;
    private bool _screenshotRequested;
    private int _selectedAgentId = -1;
    private float _autosaveTimer;
    private float _saveStatusTimer;
    private string _saveStatus = string.Empty;

    // Phase 1-4: Camera, Layout, Input
    private Camera2D _camera = null!;
    private GameLayout _layout = null!;
    private bool _isPanning;
    private Point _panStartMouse;
    private Vector2 _panStartCameraPos;

    // First Cycle: Player intervention state
    private EmergentSimulationWorld.InterventionType _selectedTool = EmergentSimulationWorld.InterventionType.None;
    private string _toolFeedback = string.Empty;
    private float _toolFeedbackTimer;

// UI Text & Scaling Foundation
    private SpriteFont _uiFont = null!;
    private float _uiScale = 1.0f;
    private string _uiScaleFeedback = string.Empty;
    private float _uiScaleFeedbackTimer;
    private FirstCycleUXState _onboarding = new();
    private bool _onboardingCompletionPersisted;
    private int _onboardingFoodBaseline;
    private int _onboardingInterventionBaseline;
    private long _selectedChronicleTick = -1;

    // UI Readability & Performance Foundation
    private bool _showDebugOverlay;
    private UICache _uiCache = new();
    private Stopwatch _drawStopwatch = new();
    private long _lastWorldDrawTicks;
    private long _lastUIDrawTicks;
    private long _lastFrameTimestamp;
    private double _fpsWindowSeconds;
    private int _fpsWindowFrames;
    private float _displayFps;
    private double _renderMetricsLogTimer;
    private int _inspectorScrollOffset;
    private readonly List<ChronicleDisplayRow> _chronicleRows = new();
    private long _chronicleRowsTick = -1;

    private sealed class ChronicleDisplayRow
    {
        public ChronicleDisplayRow(WorldEvent worldEvent)
        {
            Event = worldEvent;
        }

        public WorldEvent Event { get; }
        public int Count { get; set; } = 1;
    }

    public Game1()
    {
        _graphics = new GraphicsDeviceManager(this)
        {
            PreferredBackBufferWidth = 1280,
            PreferredBackBufferHeight = 720,
            SynchronizeWithVerticalRetrace = true,
            HardwareModeSwitch = false, // Borderless fullscreen
        };
        Content.RootDirectory = "Content";
        IsMouseVisible = true;
        IsFixedTimeStep = false;
        Window.Title = "Hollowbound - The Living Archive";
        Window.AllowUserResizing = true;
        Window.ClientSizeChanged += OnClientSizeChanged;
        _analytics.LogEvent("run_started", _world, _timeScale, _paused);
    }

    protected override void LoadContent()
    {
        _spriteBatch = new SpriteBatch(GraphicsDevice);
        _pixel = new Texture2D(GraphicsDevice, 1, 1);
        _pixel.SetData(new[] { Color.White });
        _scissorRasterizerState = new RasterizerState { ScissorTestEnable = true };

        // Load UI font
        _uiFont = Content.Load<SpriteFont>("Fonts/UIFont");

        // Load UI scale from settings
        LoadUISettings();
        ResetOnboardingBaselines();

        // Phase 3-4: Initialize camera and layout
        UpdateLayoutAndCamera();
        InitializeExperience();
    }

    private void UpdateLayoutAndCamera()
    {
        var windowRect = new Rectangle(0, 0, Window.ClientBounds.Width, Window.ClientBounds.Height);
        _layout = new GameLayout(windowRect, _uiScale);
        if (_camera is null)
        {
            _camera = new Camera2D(
                EmergentSimulationWorld.Width,
                EmergentSimulationWorld.Height,
                Camera2D.DefaultTileSize,
                _layout.WorldViewport
            );
            _camera.FitWorld();
        }
        else
        {
            _camera.SetViewport(_layout.WorldViewport);
        }
    }

    protected override void Update(GameTime gameTime)
    {
        var keyboard = Keyboard.GetState();
        var mouse = Mouse.GetState();

        if (UpdateExperienceInput(keyboard, mouse, gameTime))
        {
            _previousKeyboard = keyboard;
            _previousMouse = mouse;
            base.Update(gameTime);
            return;
        }

        if (IsPressed(keyboard, Keys.Escape) || GamePad.GetState(PlayerIndex.One).Buttons.Back == ButtonState.Pressed)
        {
            if (_selectedTool != EmergentSimulationWorld.InterventionType.None)
            {
                _selectedTool = EmergentSimulationWorld.InterventionType.None;
                _toolFeedback = "Tool cleared";
                _toolFeedbackTimer = 1f;
            }
            else
            {
                OpenExperienceMenu();
                _previousKeyboard = keyboard;
                _previousMouse = mouse;
                return;
            }
        }

        if (IsPressed(keyboard, Keys.Space))
        {
            _paused = !_paused;
            _world.ClearBacklog();
            if (!_paused)
                _onboarding.MarkTimeStarted();
            _analytics.LogEvent(_paused ? "paused" : "resumed", _world, _timeScale, _paused);
        }

        if (IsPressed(keyboard, Keys.N))
        {
            OpenExperienceMenu();
            _confirmNewWorld = true;
            _previousKeyboard = keyboard;
            _previousMouse = mouse;
            return;
        }

        if (IsPressed(keyboard, Keys.F5))
            SaveWorld(false);

        if (IsPressed(keyboard, Keys.F9))
            LoadWorld();

        // First Cycle: Tool selection (1=Bloom, 2=Beacon, 3=InsightPulse)
        if (IsPressed(keyboard, Keys.D1))
            ToggleTool(EmergentSimulationWorld.InterventionType.Bloom);
        if (IsPressed(keyboard, Keys.D2))
            ToggleTool(EmergentSimulationWorld.InterventionType.Beacon);
        if (IsPressed(keyboard, Keys.D3))
            ToggleTool(EmergentSimulationWorld.InterventionType.InsightPulse);
        if (IsPressed(keyboard, Keys.D4))
            ToggleTool(EmergentSimulationWorld.InterventionType.Passage);

        if (IsPressed(keyboard, Keys.F1))
        {
            OpenExperienceMenu();
            _previousKeyboard = keyboard;
            _previousMouse = mouse;
            return;
        }

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

        // Phase 1: F10 opens logs folder, F12 takes screenshot
        if (IsPressed(keyboard, Keys.F10))
            OpenLogsFolder();
        if (IsPressed(keyboard, Keys.F12))
            _screenshotRequested = true;

        // UI Scale: Ctrl+- and Ctrl++
        if (keyboard.IsKeyDown(Keys.LeftControl) || keyboard.IsKeyDown(Keys.RightControl))
        {
            if (IsPressed(keyboard, Keys.OemMinus) || IsPressed(keyboard, Keys.Subtract))
                SetUIScale(_uiScale - 0.25f);
            if (IsPressed(keyboard, Keys.OemPlus) || IsPressed(keyboard, Keys.Add))
                SetUIScale(_uiScale + 0.25f);
        }

        // Debug overlay toggle (F3)
        if (IsPressed(keyboard, Keys.F3))
        {
            _showDebugOverlay = !_showDebugOverlay;
            _toolFeedback = _showDebugOverlay ? "Debug overlay ON" : "Debug overlay OFF";
            _toolFeedbackTimer = 1f;
        }

        // The right panel owns its wheel input. Camera zoom is restricted to
        // WorldViewport, so scrolling the inspector never moves the world.
        var inspectorScrollDelta = mouse.ScrollWheelValue - _previousMouse.ScrollWheelValue;
        if (_layout is not null && _layout.RightPanel.Contains(mouse.Position) && inspectorScrollDelta != 0)
            _inspectorScrollOffset = Math.Max(0, _inspectorScrollOffset - Math.Sign(inspectorScrollDelta));

        // Phase 3: Camera input
        HandleCameraInput(keyboard, mouse, (float)gameTime.ElapsedGameTime.TotalSeconds);

        if (mouse.LeftButton == ButtonState.Pressed && _previousMouse.LeftButton == ButtonState.Released)
        {
            if (HandleObservatoryClick(mouse.Position))
            {
                // UI handled the click.
            }
            else if (_selectedTool != EmergentSimulationWorld.InterventionType.None)
            {
                ApplyTool(mouse.Position);
            }
            else
            {
                SelectAgent(mouse.Position);
            }
        }

        if (!_paused)
        {
            var elapsedSeconds = (float)gameTime.ElapsedGameTime.TotalSeconds;
            _world.Advance(elapsedSeconds, _timeScale);
            _autosaveTimer += elapsedSeconds;
            // Defer autosave while the world is behind its requested speed:
            // writing a snapshot during sustained catch-up would add a disk
            // hitch on top of the frame budget. The timer keeps running, so
            // the save happens on the first calm frame instead.
            if (_autosaveTimer >= 30f && !_world.IsCatchingUp || _autosaveTimer >= 120f)
                SaveWorld(true);
        }

        var onboardingWasComplete = _onboarding.IsComplete;
        _onboarding.ObserveFood(_world.FoodConsumed > _onboardingFoodBaseline);
        _onboarding.ObserveIntervention(_world.SuccessfulInterventions > _onboardingInterventionBaseline);
        if (!onboardingWasComplete && _onboarding.IsComplete)
        {
            _toolFeedback = "First observation cycle complete";
            _toolFeedbackTimer = 4f;
        }
        if (_onboarding.IsComplete && !_onboardingCompletionPersisted)
        {
            _onboardingCompletionPersisted = true;
            SaveUISettings();
        }

        _saveStatusTimer = MathF.Max(0, _saveStatusTimer - (float)gameTime.ElapsedGameTime.TotalSeconds);
        _toolFeedbackTimer = MathF.Max(0, _toolFeedbackTimer - (float)gameTime.ElapsedGameTime.TotalSeconds);
        _uiScaleFeedbackTimer = MathF.Max(0, _uiScaleFeedbackTimer - (float)gameTime.ElapsedGameTime.TotalSeconds);

        _analytics.MaybeWriteSnapshot(_world, _timeScale, _paused);
        _analytics.LogWorldEvents(_world);
        UpdateExperience(gameTime);

        _previousKeyboard = keyboard;
        _previousMouse = mouse;
        base.Update(gameTime);
    }

    protected override void Draw(GameTime gameTime)
    {
        UpdateFrameRate();
        GraphicsDevice.Clear(new Color(10, 12, 16));
        _spriteBatch.Begin(samplerState: SamplerState.PointClamp);

        _drawStopwatch.Restart();
        DrawWorld();
        _lastWorldDrawTicks = _drawStopwatch.ElapsedTicks;

        _drawStopwatch.Restart();
        DrawObservatory(gameTime);
        _lastUIDrawTicks = _drawStopwatch.ElapsedTicks;

        _renderMetricsLogTimer += gameTime.ElapsedGameTime.TotalSeconds;
        if (_renderMetricsLogTimer >= 1d)
        {
            _renderMetricsLogTimer = 0d;
            _analytics.LogRenderMetrics(
                _world,
                _timeScale,
                _paused,
                _displayFps,
                StopwatchTicksToMilliseconds(_lastWorldDrawTicks),
                StopwatchTicksToMilliseconds(_lastUIDrawTicks),
                Window.ClientBounds.Width,
                Window.ClientBounds.Height,
                _uiScale,
                _layout.WorldViewport.Width,
                _layout.WorldViewport.Height);
        }

        _spriteBatch.End();

        if (_screenshotRequested)
        {
            _screenshotRequested = false;
            TakeScreenshot();
        }
        base.Draw(gameTime);
    }

    private void DrawWorld()
    {
        var vp = _layout.WorldViewport;
        var viewMatrix = _camera.GetViewMatrix();
        var visibleBounds = _camera.GetVisibleCellBounds();
        var tileSize = Camera2D.DefaultTileSize;

        // Set scissor rectangle to clip world rendering to WorldViewport
        GraphicsDevice.ScissorRectangle = vp;
        var previousRasterizer = GraphicsDevice.RasterizerState;
        GraphicsDevice.RasterizerState = _scissorRasterizerState;

        try
        {
            _spriteBatch.End();
            _spriteBatch.Begin(
                transformMatrix: viewMatrix,
                samplerState: SamplerState.PointClamp,
                rasterizerState: _scissorRasterizerState
            );

            // Draw map background
            var mapBounds = new Rectangle(0, 0, EmergentSimulationWorld.Width * tileSize, EmergentSimulationWorld.Height * tileSize);
            DrawRect(mapBounds, new Color(22, 27, 34));
            DrawAtmosphere(visibleBounds);

            // Grid lines (only visible area + margin)
            int startX = Math.Max(0, visibleBounds.Left - (visibleBounds.Left % 4));
            int endX = Math.Min(EmergentSimulationWorld.Width, visibleBounds.Right + 4);
            for (var x = startX; x < endX; x += 4)
                if (_camera.Zoom >= 1.6f) DrawRect(new Rectangle(x * tileSize, 0, 1, mapBounds.Height), new Color(24, 33, 38));

            int startY = Math.Max(0, visibleBounds.Top - (visibleBounds.Top % 4));
            int endY = Math.Min(EmergentSimulationWorld.Height, visibleBounds.Bottom + 4);
            for (var y = startY; y < endY; y += 4)
                if (_camera.Zoom >= 1.6f) DrawRect(new Rectangle(0, y * tileSize, mapBounds.Width, 1), new Color(24, 33, 38));

            // Walls (only visible)
            foreach (var wall in _world.Map.WallCells)
            {
                if (!visibleBounds.Contains(wall)) continue;
                var rect = new Rectangle(wall.X * tileSize, wall.Y * tileSize, tileSize, tileSize);
                DrawRect(new Rectangle(rect.X + 1, rect.Y + 2, 8, 8), new Color(6, 12, 17, 175));
                DrawRect(rect, new Color(51, 66, 77));
                DrawRect(new Rectangle(rect.Left, rect.Top, rect.Width, 1), new Color(102, 127, 139));
                DrawRect(new Rectangle(rect.Left, rect.Top, 1, rect.Height), new Color(74, 94, 104));
            }

            // Doors (only visible)
            foreach (var door in _world.Map.DoorCells)
            {
                if (!visibleBounds.Contains(door)) continue;
                var rect = new Rectangle(door.X * tileSize, door.Y * tileSize, tileSize, tileSize);
                DrawRect(rect, new Color(49, 107, 82));
                DrawRect(new Rectangle(rect.Left + rect.Width / 4, rect.Top + rect.Height / 4, Math.Max(1, rect.Width / 2), Math.Max(1, rect.Height / 2)), new Color(129, 193, 137));
            }

            // Storage (only visible)
            foreach (var storage in _world.Map.StorageCells)
            {
                if (!visibleBounds.Contains(storage)) continue;
                var rect = new Rectangle(storage.X * tileSize, storage.Y * tileSize, tileSize, tileSize);
                DrawRect(rect, new Color(31, 39, 43));
                DrawRect(new Rectangle(rect.Left + rect.Width / 3, rect.Top + rect.Height / 3, Math.Max(1, rect.Width / 3), Math.Max(1, rect.Height / 3)), new Color(90, 72, 45));
            }

            // Food nodes (only visible)
            foreach (var node in _world.Food)
            {
                if (node.Amount <= 0 || !visibleBounds.Contains(node.Cell)) continue;
                var rect = new Rectangle(node.Cell.X * tileSize, node.Cell.Y * tileSize, tileSize, tileSize);
                var size = node.Amount >= 5 ? Math.Max(3, (int)(tileSize * 0.38f)) : Math.Max(2, (int)(tileSize * 0.28f));
                DrawGlow(new Vector2(rect.Center.X, rect.Center.Y), 14, new Color(223, 171, 78) * 0.3f);
                DrawRect(new Rectangle(rect.Center.X - size / 2, rect.Center.Y - size / 2, size, size), new Color(190, 145, 67));
            }

            // Food storage piles (only visible)
            foreach (var pile in _world.FoodStorage)
            {
                if (pile.Value <= 0 || !visibleBounds.Contains(pile.Key)) continue;
                var rect = new Rectangle(pile.Key.X * tileSize, pile.Key.Y * tileSize, tileSize, tileSize);
                var size = Math.Max(3, (int)(tileSize * 0.48f));
                DrawRect(new Rectangle(rect.Center.X - size / 2, rect.Center.Y - size / 2, size, size), new Color(111, 79, 44));
                DrawRect(new Rectangle(rect.Center.X - 1, rect.Center.Y - 1, 2, 2), new Color(218, 170, 78));
            }

            // First Cycle: Draw active Blooms (green glow) and Beacons (blue pulse)
            foreach (var bloom in _world.ActiveBlooms.Values)
            {
                if (!visibleBounds.Contains(bloom.Cell)) continue;
                var rect = new Rectangle(bloom.Cell.X * tileSize, bloom.Cell.Y * tileSize, tileSize, tileSize);
                var pulseSize = Math.Max(8, (int)(tileSize * 1.2f));
                var alpha = (int)(128 + 64 * MathF.Sin((float)_world.Tick * 0.1f));
                DrawRect(new Rectangle(rect.Center.X - pulseSize / 2, rect.Center.Y - pulseSize / 2, pulseSize, pulseSize), new Color((byte)80, (byte)200, (byte)80, (byte)Math.Clamp(alpha, 0, 255)));
            }
            foreach (var beacon in _world.ActiveBeacons.Values)
            {
                if (!visibleBounds.Contains(beacon.Cell)) continue;
                var rect = new Rectangle(beacon.Cell.X * tileSize, beacon.Cell.Y * tileSize, tileSize, tileSize);
                var pulseSize = Math.Max(10, (int)(tileSize * 1.5f));
                var alpha = (int)(180 + 40 * MathF.Sin((float)_world.Tick * 0.15f));
                DrawRect(new Rectangle(rect.Center.X - pulseSize / 2, rect.Center.Y - pulseSize / 2, pulseSize, pulseSize), new Color((byte)80, (byte)120, (byte)255, (byte)Math.Clamp(alpha, 0, 255)));
            }

            // Shout pulses are short-lived world signals. A square ring keeps
            // them readable at pixel scale without allocating textures or
            // introducing a second rendering system.
            foreach (var shout in _world.RecentShouts)
            {
                if (shout.ExpiresTick <= _world.Tick || !visibleBounds.Contains(shout.Cell))
                    continue;

                var center = new Point(shout.Cell.X * tileSize + tileSize / 2, shout.Cell.Y * tileSize + tileSize / 2);
                var radius = Math.Max(3, (int)(shout.Radius * tileSize * 0.18f));
                var alpha = (byte)Math.Clamp(210 - (int)((shout.ExpiresTick - _world.Tick) * 5), 55, 210);
                var color = shout.Type switch
                {
                    ShoutType.Food => new Color((byte)226, (byte)184, (byte)78, alpha),
                    ShoutType.Danger => new Color((byte)210, (byte)86, (byte)78, alpha),
                    _ => new Color((byte)91, (byte)171, (byte)190, alpha),
                };
                DrawRect(new Rectangle(center.X - radius, center.Y - radius, radius * 2 + 1, 1), color);
                DrawRect(new Rectangle(center.X - radius, center.Y + radius, radius * 2 + 1, 1), color);
                DrawRect(new Rectangle(center.X - radius, center.Y - radius, 1, radius * 2 + 1), color);
                DrawRect(new Rectangle(center.X + radius, center.Y - radius, 1, radius * 2 + 1), color);
            }

            // Agents (only visible)
            foreach (var agent in _world.Agents)
            {
                if (!agent.Alive || !visibleBounds.Contains(agent.Cell)) continue;
                var factionColor = FactionTint(agent.FactionId);
                var rect = new Rectangle(agent.Cell.X * tileSize, agent.Cell.Y * tileSize, tileSize, tileSize);
                var size = Math.Max(6, (int)(tileSize * 0.72f));
                var agentRect = new Rectangle(rect.Center.X - size / 2, rect.Center.Y - size / 2, size, size);
                DrawGlow(new Vector2(rect.Center.X, rect.Center.Y), 12, factionColor * 0.4f);
                DrawRect(agentRect, agent.Energy < 25 ? UITheme.CriticalText : factionColor);
                DrawRect(agentRect, new Color(18, 22, 28), 1);
                var markerSize = Math.Max(2, size / 4);
                var markerX = rect.Center.X + Math.Clamp(agent.Facing.X, -1, 1) * Math.Max(1, size / 4) - markerSize / 2;
                var markerY = rect.Center.Y + Math.Clamp(agent.Facing.Y, -1, 1) * Math.Max(1, size / 4) - markerSize / 2;
                DrawRect(new Rectangle(markerX, markerY, markerSize, markerSize), new Color(226, 211, 154));
                if (agent.CarriedFood > 0)
                    DrawRect(new Rectangle(agentRect.Right, agentRect.Bottom - 2, 2, 2), new Color(255, 210, 106));

                if (agent.Id == _selectedAgentId)
                    DrawRect(new Rectangle(agentRect.Left - 3, agentRect.Top - 3, agentRect.Width + 6, agentRect.Height + 6), new Color(228, 220, 163), 2);
            }
            DrawWorldAnnotations(visibleBounds);
        }
        finally
        {
            _spriteBatch.End();
            _spriteBatch.Begin(samplerState: SamplerState.PointClamp, rasterizerState: previousRasterizer);
            GraphicsDevice.RasterizerState = previousRasterizer;
        }
    }


    private void RefreshChronicleRows()
    {
        if (_chronicleRowsTick == _world.Tick)
            return;

        _chronicleRowsTick = _world.Tick;
        _chronicleRows.Clear();
        var grouped = new Dictionary<(long tick, WorldEventType type, int factionId), ChronicleDisplayRow>();

        // Chronicle is ordered oldest to newest. Walk backwards so the first
        // display row is always the newest event group. Grouping is limited to
        // the bounded Chronicle buffer and never mutates simulation state.
        var events = _world.Chronicle;
        for (var index = events.Count - 1; index >= 0; index--)
        {
            var worldEvent = events[index];
            if (!_showAllEvents && worldEvent.Importance == WorldEventImportance.Minor) continue;
            var key = (worldEvent.Tick, worldEvent.Type, worldEvent.FactionId);
            if (grouped.TryGetValue(key, out var existing))
            {
                existing.Count++;
                continue;
            }

            var row = new ChronicleDisplayRow(worldEvent);
            grouped.Add(key, row);
            _chronicleRows.Add(row);
        }
    }


    private void FocusChronicleEvent(WorldEvent worldEvent)
    {
        if (worldEvent.HasCell && _world.Map.InBounds(worldEvent.Cell))
        {
            _camera.CenterOnCell(worldEvent.Cell);
            return;
        }

        var factionAgent = worldEvent.FactionId >= 0
            ? _world.Agents.Where(agent => agent.Alive && agent.FactionId == worldEvent.FactionId).OrderBy(agent => agent.Id).FirstOrDefault()
            : null;
        var fallbackAgent = factionAgent ?? _world.Agents.Where(agent => agent.Alive).OrderBy(agent => agent.Id).FirstOrDefault();
        if (fallbackAgent is not null)
        {
            _selectedAgentId = fallbackAgent.Id;
            _inspectorScrollOffset = 0;
            _camera.CenterOnCell(fallbackAgent.Cell);
        }
    }

    private void ToggleTool(EmergentSimulationWorld.InterventionType tool)
    {
        if (_selectedTool == tool)
        {
            _selectedTool = EmergentSimulationWorld.InterventionType.None;
            _toolFeedback = "Tool cleared";
        }
        else if (_world.AvailableResonance < (tool == EmergentSimulationWorld.InterventionType.Passage ? 2 : 1))
        {
            _toolFeedback = "Not enough Resonance";
        }
        else
        {
            _selectedTool = tool;
            _toolFeedback = tool switch
            {
                EmergentSimulationWorld.InterventionType.Bloom => "Bloom selected: click food or open ground",
                EmergentSimulationWorld.InterventionType.Beacon => "Beacon selected: click a walkable target",
                EmergentSimulationWorld.InterventionType.InsightPulse => "Insight selected: click near agents",
                EmergentSimulationWorld.InterventionType.Passage => "Проход: выберите блок стены. Стоимость — 2 резонанса.",
                _ => "Tool selected",
            };
        }
        _toolFeedbackTimer = 2f;
    }

    private void SelectAgent(Point mousePosition)
    {
        // Use camera and layout for coordinate conversion
        if (!_layout.IsPointInWorldViewport(mousePosition))
        {
            _selectedAgentId = -1;
            _inspectorScrollOffset = 0;
            return;
        }

        // Convert screen position to viewport-relative
        var viewportPos = new Vector2(
            mousePosition.X - _layout.WorldViewport.Left,
            mousePosition.Y - _layout.WorldViewport.Top
        );

        if (!_camera.TryScreenToCell(viewportPos, out var cell))
        {
            _selectedAgentId = -1;
            _inspectorScrollOffset = 0;
            return;
        }

        var nearest = _world.Agents
            .Where(agent => agent.Alive)
            .OrderBy(agent => Math.Abs(agent.Cell.X - cell.X) + Math.Abs(agent.Cell.Y - cell.Y))
            .FirstOrDefault();

        _selectedAgentId = nearest is not null && Math.Abs(nearest.Cell.X - cell.X) <= 1 && Math.Abs(nearest.Cell.Y - cell.Y) <= 1
            ? nearest.Id
            : -1;
        _inspectorScrollOffset = 0;
        if (_selectedAgentId >= 0)
            _onboarding.MarkAgentSelected();
    }

    private void ToggleFullscreen()
    {
        var enterFullscreen = !_graphics.IsFullScreen;
        if (enterFullscreen)
        {
            // Save windowed size before going fullscreen
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

        _windowModeTransition = true;
        _isFullscreen = enterFullscreen;
        try
        {
            _graphics.ApplyChanges();
            _isFullscreen = _graphics.IsFullScreen;
        }
        finally
        {
            _windowModeTransition = false;
            if (Window.ClientBounds.Width > 0 && Window.ClientBounds.Height > 0)
                UpdateLayoutAndCamera();
        }
    }

    private void OnClientSizeChanged(object? sender, EventArgs e)
    {
        // Only track windowed size when not in fullscreen
        if (!_windowModeTransition && !_graphics.IsFullScreen && Window.ClientBounds.Width > 0 && Window.ClientBounds.Height > 0)
        {
            _windowedWidth = Math.Max(640, Window.ClientBounds.Width);
            _windowedHeight = Math.Max(360, Window.ClientBounds.Height);
        }

        // Update layout and camera when window size changes
        if (!_windowModeTransition && Window.ClientBounds.Width > 0 && Window.ClientBounds.Height > 0)
            UpdateLayoutAndCamera();
    }

    private void ResetWorld()
    {
        _world = new EmergentSimulationWorld(Random.Shared.Next(1, int.MaxValue), initialPopulation: 2);
        _timeScale = 1f;
        _paused = true;
        _autosaveTimer = 0;
        _selectedAgentId = -1;
        _inspectorScrollOffset = 0;
        _chronicleRowsTick = -1;
        ResetOnboardingBaselines();
        _analytics.LogEvent("new_world", _world, _timeScale, _paused);
    }

    private void SaveWorld(bool automatic)
    {
        // Snapshot creation, serialization and disk write all run on the UI
        // thread; measure the cost so autosave hitches are visible in analytics
        // instead of being silent frame spikes.
        var stopwatch = Stopwatch.StartNew();
        try
        {
            WorldSaveService.Save(_world, SessionSavePath);
            stopwatch.Stop();
            _autosaveTimer = 0;
            _saveStatus = automatic ? "AUTO-SAVED" : "SAVED";
            _saveStatusTimer = 3f;
            _analytics.LogEvent("saved", _world, _timeScale, _paused,
                $"path={SessionSavePath}; duration_ms={stopwatch.Elapsed.TotalMilliseconds:F1}");
        }
        catch (Exception exception)
        {
            stopwatch.Stop();
            _saveStatus = "SAVE ERROR";
            _saveStatusTimer = 4f;
            _analytics.LogEvent("save_failed", _world, _timeScale, _paused,
                $"{exception.GetType().Name}; duration_ms={stopwatch.Elapsed.TotalMilliseconds:F1}");
        }
    }

    private void LoadWorld()
    {
        try
        {
            _world = WorldSaveService.Load(SessionSavePath);
            _world.ClearBacklog();
            _timeScale = 1f;
            _paused = true;
            _autosaveTimer = 0;
            _selectedAgentId = -1;
            _inspectorScrollOffset = 0;
            _chronicleRowsTick = -1;
            ResetOnboardingBaselines();
            _saveStatus = "LOADED";
            _saveStatusTimer = 3f;
            _analytics.LogEvent("loaded", _world, _timeScale, _paused, WorldSaveService.DefaultPath);
        }
        catch (Exception exception)
        {
            _saveStatus = "LOAD ERROR";
            _saveStatusTimer = 4f;
            _analytics.LogEvent("load_failed", _world, _timeScale, _paused, exception.GetType().Name);
        }
    }

    private void ApplyTool(Point mousePosition)
        {
            // Use camera and layout for coordinate conversion
            if (!_layout.IsPointInWorldViewport(mousePosition))
            {
                _toolFeedback = "Invalid: Click on world viewport";
                _toolFeedbackTimer = 2f;
                return;
            }

            // Convert screen position to viewport-relative
            var viewportPos = new Vector2(
                mousePosition.X - _layout.WorldViewport.Left,
                mousePosition.Y - _layout.WorldViewport.Top
            );

            if (!_camera.TryScreenToCell(viewportPos, out var cell))
            {
                _toolFeedback = "Invalid: Click on map";
                _toolFeedbackTimer = 2f;
                return;
            }

            var (success, reason) = _world.QueueIntervention(_selectedTool, cell);

            if (success)
            {
                _toolFeedback = _paused ? "Вмешательство в очереди. Нажмите «Продолжить»." : "Вмешательство принято — наблюдайте за агентами.";
                _analytics.LogEvent("intervention_queued", _world, _timeScale, _paused, $"{_selectedTool} at {cell}");
            }
            else
            {
                _toolFeedback = $"Failed: {reason}";
            }
            _toolFeedbackTimer = 2f;
        }

    private void OpenLogsFolder()
    {
        try
        {
            var logDirectory = _analytics.LogPath;
            if (!string.IsNullOrEmpty(logDirectory))
            {
                var directory = Path.GetDirectoryName(logDirectory);
                if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = directory,
                        UseShellExecute = true,
                        Verb = "open"
                    });
                    _toolFeedback = "Opened logs folder";
                }
                else
                {
                    _toolFeedback = "Logs folder not found";
                }
            }
            else
            {
                _toolFeedback = "No active log file";
            }
        }
        catch (Exception ex)
        {
            _toolFeedback = $"Failed to open logs: {ex.Message}";
            _analytics.LogEvent("logs_open_failed", _world, _timeScale, _paused, ex.Message);
        }
        _toolFeedbackTimer = 2f;
    }

    private void TakeScreenshot()
    {
        try
        {
            var screenshotsDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
                "Hollowbound", "Screenshots");
            Directory.CreateDirectory(screenshotsDir);

            var fileName = $"hollowbound-{DateTime.UtcNow:yyyyMMdd-HHmmssfff}.png";
            var filePath = Path.Combine(screenshotsDir, fileName);

            var width = GraphicsDevice.PresentationParameters.BackBufferWidth;
            var height = GraphicsDevice.PresentationParameters.BackBufferHeight;
            var pixels = new Color[width * height];
            GraphicsDevice.GetBackBufferData(pixels);
            using var screenshot = new Texture2D(GraphicsDevice, width, height, false, SurfaceFormat.Color);
            screenshot.SetData(pixels);

            using var stream = File.OpenWrite(filePath);
            screenshot.SaveAsPng(stream, width, height);

            _toolFeedback = $"Screenshot saved: {fileName}";
            _analytics.LogEvent("screenshot", _world, _timeScale, _paused, filePath);
        }
        catch (Exception ex)
        {
            _toolFeedback = $"Screenshot failed: {ex.Message}";
            _analytics.LogEvent("screenshot_failed", _world, _timeScale, _paused, ex.Message);
        }
        _toolFeedbackTimer = 2f;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (_previewPath is null && !_isolatedPlaytest) SaveWorld(true);
            _glowTexture?.Dispose();
            _analytics.Complete(_world, _timeScale, _paused, "application_closed");
            _scissorRasterizerState?.Dispose();
            _analytics.Dispose();
        }
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


    private void HandleCameraInput(KeyboardState keyboard, MouseState mouse, float elapsedSeconds)
    {
        var viewport = _layout.WorldViewport;
        var cameraMoved = false;

        // WASD pan in screen pixels per second; Camera2D applies zoom exactly once.
        var direction = Vector2.Zero;
        if (keyboard.IsKeyDown(Keys.W))
            direction.Y -= 1;
        if (keyboard.IsKeyDown(Keys.S))
            direction.Y += 1;
        if (keyboard.IsKeyDown(Keys.A))
            direction.X -= 1;
        if (keyboard.IsKeyDown(Keys.D))
            direction.X += 1;
        if (direction != Vector2.Zero)
        {
            direction.Normalize();
            _camera.Pan(direction * 480f * Math.Clamp(elapsedSeconds, 0f, 0.1f));
            cameraMoved = true;
        }

        // Middle mouse drag pan
        if (mouse.MiddleButton == ButtonState.Pressed && (_isPanning || viewport.Contains(mouse.Position)))
        {
            if (!_isPanning)
            {
                _isPanning = true;
                _panStartMouse = mouse.Position;
                _panStartCameraPos = _camera.Position;
            }
            else
            {
                var delta = mouse.Position - _panStartMouse;
                _camera.Position = _panStartCameraPos - new Vector2(delta.X, delta.Y) / _camera.Zoom;
                cameraMoved |= delta.X != 0 || delta.Y != 0;
                // ClampToMap is called in Position setter
            }
        }
        else
        {
            _isPanning = false;
        }

        // Mouse wheel zoom (relative to cursor)
        var scrollDelta = mouse.ScrollWheelValue - _previousMouse.ScrollWheelValue;
        if (scrollDelta != 0)
        {
            var mouseInViewport = _layout.IsPointInWorldViewport(mouse.Position);
            if (mouseInViewport)
            {
                var viewportPos = new Vector2(
                    mouse.Position.X - viewport.Left,
                    mouse.Position.Y - viewport.Top
                );
                var factor = scrollDelta > 0 ? 1.1f : 1f / 1.1f;
                _camera.ZoomAt(factor, viewportPos);
                cameraMoved = true;
            }
        }

        // Home - fit world
        if (IsPressed(keyboard, Keys.Home))
        {
            _camera.FitWorld();
            cameraMoved = true;
        }

        // F - focus selected agent
        if (IsPressed(keyboard, Keys.F))
        {
            var selected = _world.Agents.FirstOrDefault(a => a.Id == _selectedAgentId);
            if (selected is not null && selected.Alive)
                _camera.CenterOnCell(selected.Cell);
        }

        if (cameraMoved)
            _onboarding.MarkCameraMoved();
    }

    // UI Text & Scaling Foundation - Settings persistence
    private void LoadUISettings()
    {
        var settings = UISettingsStore.Load(GetUISettingsPath());
        _uiScale = UITheme.ClampScale(settings.UiScale);
        _onboarding = new FirstCycleUXState(settings.OnboardingCompleted);
        _onboardingCompletionPersisted = settings.OnboardingCompleted;
    }

    private void SaveUISettings()
    {
        if (_isolatedPlaytest || _previewPath is not null) return;
        try
        {
            UISettingsStore.Save(GetUISettingsPath(), new UISettingsData
            {
                UiScale = _uiScale,
                OnboardingCompleted = _onboarding.IsComplete,
            });
        }
        catch (Exception ex)
        {
            _analytics.LogEvent("settings_save_failed", _world, _timeScale, _paused, ex.Message);
        }
    }

    private static string GetUISettingsPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Hollowbound",
        "settings.json");

    // UI Readability & Performance Foundation - UI Cache
    private sealed class UICache
    {
        public int StoredFood { get; private set; }
        public void MaybeUpdate(EmergentSimulationWorld world, int selectedAgentId, float uiScale)
        {
            StoredFood = world.StoredFoodUnits;
        }
    }

    private void ResetOnboardingBaselines()
    {
        _onboardingFoodBaseline = _world.FoodConsumed;
        _onboardingInterventionBaseline = _world.SuccessfulInterventions;
    }

    private void SetUIScale(float newScale)
    {
        var clamped = UITheme.ClampScale(newScale);
        if (Math.Abs(clamped - _uiScale) < 0.001f) return;

        _uiScale = clamped;

        // Update layout with new scale - preserves camera position/zoom
        _layout = _layout.WithUIScale(_uiScale);
        _camera.SetViewport(_layout.WorldViewport);

        SaveUISettings();

        _uiScaleFeedback = $"UI Scale: {(_uiScale * 100):0}%";
        _uiScaleFeedbackTimer = 2f;
    }

    // Replace old bitmap DrawText with SpriteFont version
    private void DrawText(string text, Vector2 position, Color color, float scale = 1f)
    {
        if (_uiFont is null || string.IsNullOrEmpty(text)) return;
        _spriteBatch.DrawString(_uiFont, text, position, color, 0f, Vector2.Zero, scale, SpriteEffects.None, 0f);
    }

    private void DrawTextClipped(string text, Vector2 position, Color color, float maxWidth, float scale = 1f, UIPrimitives.TextAlign align = UIPrimitives.TextAlign.Left)
    {
        if (_uiFont is null || string.IsNullOrEmpty(text)) return;
        UIPrimitives.DrawTextClipped(_spriteBatch, _uiFont, text, position, color, maxWidth, scale, align);
    }

    private bool IsPressed(KeyboardState current, Keys key) => current.IsKeyDown(key) && !_previousKeyboard.IsKeyDown(key);

    // UI Readability & Performance Foundation - Debug Overlay

    private void UpdateFrameRate()
    {
        var now = Stopwatch.GetTimestamp();
        if (_lastFrameTimestamp != 0)
        {
            var elapsed = (now - _lastFrameTimestamp) / (double)Stopwatch.Frequency;
            if (elapsed > 0d && elapsed <= 2d)
            {
                _fpsWindowSeconds += elapsed;
                _fpsWindowFrames++;
                if (_fpsWindowSeconds >= 0.25d)
                {
                    _displayFps = (float)(_fpsWindowFrames / _fpsWindowSeconds);
                    _fpsWindowSeconds = 0d;
                    _fpsWindowFrames = 0;
                }
            }
        }

        _lastFrameTimestamp = now;
    }

    private static double StopwatchTicksToMilliseconds(long ticks) =>
        ticks * 1000d / Stopwatch.Frequency;
}
