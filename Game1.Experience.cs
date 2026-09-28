using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using Hollowbound.Simulation;

namespace Hollowbound;

// Presentation and player-facing interaction. Simulation state changes only via
// the existing intervention queue; camera, charts and map lenses are observers.
public sealed partial class Game1
{
    private static readonly Color Mint = new(117, 225, 198);
    private static readonly Color Gold = new(233, 190, 119);
    private static readonly Color Ink = new(10, 18, 24);
    private static readonly Color Muted = new(145, 166, 174);
    private static readonly float[] Speeds = { 1, 5, 10, 25, 50, 100, 250, 500 };
    private Texture2D? _glowTexture;
    private bool _menuOpen, _confirmNewWorld, _followAgent;
    private int _inspectorTab, _mapLens, _chronicleScroll;
    private float _experienceTimer;
    private EmergentSimulationWorld? _observedWorld;
    private long _chartTick = -1;
    private readonly Queue<int> _populationHistory = new();
    private readonly List<SettlementState> _livingSettlements = new();
    private readonly int[] _density = new int[16 * 10];
    private readonly float[] _energyDensity = new float[16 * 10];
    private readonly int[] _factionDensity = new int[16 * 10];
    private AgentState? _inspectedAgent;
    private string? _previewPath;
    private bool _isolatedPlaytest;
    private int _previewTicks, _previewFrames;
    private float _previewScale = 1f;
    private int _previewLens;
    private double _visualTime;
    private Rectangle _debugBounds;
    private bool _showAllEvents;
    private readonly Dictionary<(string Text, int Width, float Scale), List<string>> _wrapCache = new();
    private readonly List<string> _analyticsWrappedLines = new();
    private long _analyticsWrappedRevision = -1;
    private int _analyticsWrappedWidth = -1;
    private float _analyticsWrappedScale = -1f;

    public void ConfigurePlaytest()
    {
        _isolatedPlaytest = true;
        _world = new EmergentSimulationWorld(12345, 80);
        _world.FrameSimulationBudgetMilliseconds = 200;
        while (_world.Tick < 1500) _world.Advance(.1f, 1);
        _world.ClearBacklog();
        _world.FrameSimulationBudgetMilliseconds = 6;
    }

    private string SessionSavePath => _isolatedPlaytest
        ? Path.Combine(Path.GetTempPath(), "hollowbound-playtest-save.json") : WorldSaveService.DefaultPath;

    public void ConfigurePreview(string path, int ticks, int population, int width, int height, float scale, int lens)
    {
        _previewPath = Path.GetFullPath(path);
        _previewTicks = Math.Clamp(ticks, 0, 20000);
        _previewScale = UITheme.ClampScale(scale);
        _previewLens = Math.Clamp(lens, 0, 3);
        _world = new EmergentSimulationWorld(12345, Math.Clamp(population, 2, 300));
        _graphics.PreferredBackBufferWidth = Math.Clamp(width, 640, 2560);
        _graphics.PreferredBackBufferHeight = Math.Clamp(height, 360, 1440);
    }

    private void InitializeExperience()
    {
        const int size = 48;
        var colors = new Color[size * size];
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
        {
            var d = Vector2.Distance(new Vector2(x + .5f, y + .5f), new Vector2(size / 2f)) / (size / 2f);
            colors[y * size + x] = Color.White * MathF.Pow(Math.Max(0, 1 - d), 2.5f);
        }
        _glowTexture = new Texture2D(GraphicsDevice, size, size);
        _glowTexture.SetData(colors);
        if (_previewPath is not null)
        {
            _uiScale = _previewScale;
            _mapLens = _previewLens;
            _world.FrameSimulationBudgetMilliseconds = 200;
            while (_world.Tick < _previewTicks)
                _world.Advance(.1f, 1f);
            _world.ClearBacklog();
            _world.FrameSimulationBudgetMilliseconds = 6;
            _onboarding = new FirstCycleUXState(true);
            _selectedAgentId = _world.Agents.FirstOrDefault(a => a.Alive)?.Id ?? -1;
            UpdateLayoutAndCamera();
            _camera.FitWorld();
        }
        RefreshExperience();
    }

    private void RefreshExperience()
    {
        if (!ReferenceEquals(_observedWorld, _world))
        {
            _observedWorld = _world;
            _populationHistory.Clear();
            _chartTick = -1;
            _chronicleRowsTick = -1;
            _chronicleScroll = _inspectorScrollOffset = 0;
            _followAgent = false;
        }
        _inspectedAgent = _world.Agents.FirstOrDefault(a => a.Id == _selectedAgentId);
        _livingSettlements.Clear();
        _livingSettlements.AddRange(_world.Settlements.Values.Where(s => !s.IsAbandoned && s.Population > 0)
            .OrderByDescending(s => s.Population).ThenBy(s => s.Id));
        Array.Clear(_density);
        Array.Clear(_energyDensity);
        Array.Clear(_factionDensity);
        foreach (var a in _world.Agents)
        {
            if (!a.Alive) continue;
            var i = Math.Clamp(a.Cell.Y / 8, 0, 9) * 16 + Math.Clamp(a.Cell.X / 8, 0, 15);
            _density[i]++;
            _energyDensity[i] += a.Energy;
            _factionDensity[i] += a.FactionId == 0 ? 1 : -1;
        }
        if (_chartTick < 0 || _world.Tick - _chartTick >= 100)
        {
            _chartTick = _world.Tick;
            _populationHistory.Enqueue(_world.AlivePopulation);
            if (_populationHistory.Count > 100) _populationHistory.Dequeue();
        }
        _uiCache.MaybeUpdate(_world, _selectedAgentId, _uiScale);
        RefreshChronicleRows();
    }

    private void UpdateExperience(GameTime time)
    {
        _experienceTimer += (float)time.ElapsedGameTime.TotalSeconds;
        if (_experienceTimer >= .2f || !ReferenceEquals(_observedWorld, _world) || _inspectedAgent?.Id != _selectedAgentId)
        {
            _experienceTimer = 0;
            RefreshExperience();
        }
        if (_followAgent && _inspectedAgent is { Alive: true }) _camera.CenterOnCell(_inspectedAgent.Cell);
    }

    private bool UpdateExperienceInput(KeyboardState keyboard, MouseState mouse, GameTime time)
    {
        if (_previewPath is not null) return true;
        if (_menuOpen)
        {
            if (IsPressed(keyboard, Keys.Escape)) { _menuOpen = false; _confirmNewWorld = false; }
            else if (mouse.LeftButton == ButtonState.Pressed && _previousMouse.LeftButton == ButtonState.Released)
                HandleMenuClick(mouse.Position);
            return true;
        }
        if (IsPressed(keyboard, Keys.M)) _mapLens = (_mapLens + 1) % 4;
        if (mouse.RightButton == ButtonState.Pressed && _previousMouse.RightButton == ButtonState.Released)
            _selectedTool = EmergentSimulationWorld.InterventionType.None;
        if (keyboard.IsKeyDown(Keys.W) || keyboard.IsKeyDown(Keys.A) || keyboard.IsKeyDown(Keys.S) || keyboard.IsKeyDown(Keys.D) || mouse.MiddleButton == ButtonState.Pressed)
            _followAgent = false;
        if (IsPressed(keyboard, Keys.G) && _world.Chronicle.LastOrDefault() is { } evt) FocusChronicleEvent(evt);
        if (IsPressed(keyboard, Keys.H) && _livingSettlements.FirstOrDefault() is { } settlement) _camera.CenterOnCell(settlement.CenterCell);
        var wheel = mouse.ScrollWheelValue - _previousMouse.ScrollWheelValue;
        if (wheel != 0 && _layout.BottomPanel.Contains(mouse.Position))
            _chronicleScroll = Math.Clamp(_chronicleScroll - Math.Sign(wheel), 0, Math.Max(0, _chronicleRows.Count - 2));
        return false;
    }

    private void OpenExperienceMenu()
    {
        _menuOpen = true;
        _paused = true;
        _world.ClearBacklog();
        _analytics.LogEvent("menu_opened", _world, _timeScale, _paused);
    }

    private void SetPaused(bool paused)
    {
        _paused = paused;
        _world.ClearBacklog();
        if (!paused) _onboarding.MarkTimeStarted();
        _analytics.LogEvent(paused ? "paused" : "resumed", _world, _timeScale, paused);
    }

    private void ChangeSpeed(int direction)
    {
        var index = Array.IndexOf(Speeds, _timeScale);
        _timeScale = Speeds[Math.Clamp(index + direction, 0, Speeds.Length - 1)];
        _world.ClearBacklog();
        _analytics.LogEvent("speed_changed", _world, _timeScale, _paused);
    }

    private Rectangle TopControl(int index)
    {
        var s = _uiScale;
        var w = Math.Max(48, (int)(70 * s));
        return new Rectangle(_layout.TopBar.Right - 12 - (4 - index) * (w + 5), 8, w,
            Math.Max(1, Math.Min((int)(32 * s), _layout.TopBar.Height - 32)));
    }

    private Rectangle ToolCard(int index)
    {
        var p = _layout.LeftPanel;
        var compact = p.Height < 350;
        var startY = compact ? 55 : 70;
        var gap = compact ? 4 : 10;
        var h = compact
            ? Math.Max(18, (p.Height - startY - 8 - gap * 3) / 4)
            : Math.Clamp((p.Height - 115) / 6, 28, (int)(82 * _uiScale));
        return new Rectangle(p.X + 12, p.Y + startY + index * (h + gap), Math.Max(1, p.Width - 24), h);
    }

    private Rectangle MiniMapBounds()
    {
        var p = _layout.BottomPanel;
        var h = Math.Max(1, p.Height - 26);
        var w = Math.Min(_layout.LeftPanel.Width - 24, h * 128 / 80);
        return new Rectangle(12, p.Y + 17, Math.Max(1, w), Math.Max(1, w * 80 / 128));
    }

    private Rectangle LensButton(int index)
    {
        var vp = _layout.WorldViewport;
        var width = Math.Min(105, Math.Max(36, (vp.Width - 28) / 4));
        return new Rectangle(vp.X + 10 + index * (width + 2), vp.Y + 10, width, 28);
    }

    private Rectangle TabButton(int index)
    {
        var p = _layout.RightPanel;
        var w = Math.Max(1, (p.Width - 24) / 3);
        return new Rectangle(p.X + 12 + index * w, p.Y + 12, w, 30);
    }

    private Rectangle ChronicleRow(int index)
    {
        var p = _layout.BottomPanel;
        var x = _layout.LeftPanel.Right + 12;
        var h = Math.Max(22, (p.Height - 38) / 2);
        return new Rectangle(x, p.Y + 30 + index * h, Math.Max(1, p.Right - x - 16), h);
    }

    private bool HandleObservatoryClick(Point p)
    {
        for (var i = 0; i < 4; i++)
        {
            if (TopControl(i).Contains(p))
            {
                if (i == 0) SetPaused(!_paused);
                if (i == 1) ChangeSpeed(-1);
                if (i == 2) ChangeSpeed(1);
                if (i == 3) OpenExperienceMenu();
                return true;
            }
            if (LensButton(i).Contains(p)) { _mapLens = i; return true; }
        }
        if (_showDebugOverlay && _debugBounds.Contains(p)) return true;
        for (var i = 0; i < 4; i++)
        {
            if (ToolCard(i).Contains(p)) { ToggleTool((EmergentSimulationWorld.InterventionType)(i + 1)); return true; }
            if (i < 3 && TabButton(i).Contains(p)) { _inspectorTab = i; _inspectorScrollOffset = 0; return true; }
        }
        var mini = MiniMapBounds();
        if (mini.Contains(p))
        {
            _camera.CenterOnCell(new Point((p.X - mini.X) * 128 / mini.Width, (p.Y - mini.Y) * 80 / mini.Height));
            _onboarding.MarkCameraMoved();
            _followAgent = false;
            return true;
        }
        if (_layout.BottomPanel.Contains(p))
        {
            if (ArchiveFilter().Contains(p)) { _showAllEvents = !_showAllEvents; _chronicleRowsTick = -1; _chronicleScroll = 0; RefreshChronicleRows(); return true; }
            for (var i = 0; i < 2; i++)
            {
                var index = _chronicleScroll + i;
                if (index < _chronicleRows.Count && ChronicleRow(i).Contains(p))
                {
                    FocusChronicleEvent(_chronicleRows[index].Event);
                    _selectedChronicleTick = _chronicleRows[index].Event.Tick;
                    _onboarding.MarkChronicleOpened();
                }
            }
            return true;
        }
        if (_layout.RightPanel.Contains(p))
        {
            if (_inspectorTab == 1)
            {
                var row = (p.Y - _layout.RightPanel.Y - 90) / 76 + _inspectorScrollOffset;
                if (p.Y >= _layout.RightPanel.Y + 90 && row >= 0 && row < _livingSettlements.Count)
                {
                    _followAgent = false;
                    _camera.CenterOnCell(_livingSettlements[row].CenterCell);
                }
            }
            else if (_inspectorTab == 0 && FollowButton().Contains(p)) _followAgent = !_followAgent;
            return true;
        }
        return _layout.LeftPanel.Contains(p) || _layout.TopBar.Contains(p);
    }

    private void DrawObservatory(GameTime gameTime)
    {
        _visualTime += gameTime.ElapsedGameTime.TotalSeconds;
        var s = _uiScale;
        foreach (var panel in new[] { _layout.TopBar, _layout.LeftPanel, _layout.RightPanel, _layout.BottomPanel })
            UIPrimitives.DrawPanel(_spriteBatch, _pixel, panel, UITheme.PanelBg, UITheme.PanelBorder);
        var controlLeft = TopControl(0).Left;
        Text("HOLLOWBOUND", new Rectangle(16, 9, Math.Max(1, controlLeft - 28), 28), Mint, 1.05f * s);
        if (controlLeft > 450)
            Text("THE LIVING ARCHIVE  /  наблюдай · направляй · открывай", new Rectangle(196, 15, controlLeft - 210, 24), Muted, .67f * s);
        Button(TopControl(0), _paused ? "Старт" : "Пауза", _paused ? Mint : Gold, !_paused);
        Button(TopControl(1), "-", Muted);
        Button(TopControl(2), "+", Muted);
        Button(TopControl(3), "Меню", Mint);
        var metricsY = Math.Min(_layout.TopBar.Height - 22, Math.Max(44, (int)(44 * s)));
        var strip = new Rectangle(16, metricsY, _layout.TopBar.Width - 32, Math.Max(16, _layout.TopBar.Height - metricsY - 3));
        Text($"ЖИТЕЛИ  {_world.AlivePopulation:N0}     ЗАПАСЫ  {_uiCache.StoredFood:N0}     РЕЗОНАНС  {_world.Resonance}/10     СЧЁТ  {_world.ScoreTotal:0}     {_timeScale:0}x / реально {_world.SmoothedActualSpeed:0.#}x", strip, UITheme.PrimaryText, .78f * s);

        Clip(_layout.LeftPanel, DrawInterventionDeck);
        Clip(_layout.RightPanel, DrawInspectorDeck);
        Clip(_layout.BottomPanel, DrawArchive);
        for (var i = 0; i < 4; i++)
            Button(LensButton(i), new[] { "Мир", "Пища", "Энергия", "Фракции" }[i], i == _mapLens ? Mint : Muted, i == _mapLens);
        var vp = _layout.WorldViewport;
        if (_paused && !_menuOpen)
            Text("ПАУЗА  /  Space — продолжить", new Rectangle(vp.X + 14, vp.Bottom - 28, vp.Width - 28, 24), Gold, .75f);
        if (_toolFeedbackTimer > 0 || _saveStatusTimer > 0 || _uiScaleFeedbackTimer > 0)
        {
            var message = _toolFeedbackTimer > 0 ? _toolFeedback : _saveStatusTimer > 0 ? _saveStatus : _uiScaleFeedback;
            var toast = new Rectangle(vp.X + 14, vp.Bottom - 68, Math.Max(1, vp.Width - 28), 32);
            UIPrimitives.DrawPanel(_spriteBatch, _pixel, toast, Ink, Mint * .45f);
            Text(message, Inset(toast, 7), Gold, .72f);
        }
        if (_showDebugOverlay) DrawExperienceDebug();
        if (_menuOpen) DrawExperienceMenu();
        if (_previewPath is not null && ++_previewFrames >= 3)
        {
            _spriteBatch.End();
            var width = GraphicsDevice.PresentationParameters.BackBufferWidth;
            var height = GraphicsDevice.PresentationParameters.BackBufferHeight;
            var data = new Color[width * height];
            GraphicsDevice.GetBackBufferData(data);
            using var texture = new Texture2D(GraphicsDevice, width, height);
            texture.SetData(data);
            Directory.CreateDirectory(Path.GetDirectoryName(_previewPath)!);
            using (var stream = File.Create(_previewPath)) texture.SaveAsPng(stream, width, height);
            _spriteBatch.Begin(samplerState: SamplerState.PointClamp);
            Exit();
        }
    }

    private void DrawInterventionDeck()
    {
        var p = _layout.LeftPanel;
        var s = Math.Min(_uiScale, p.Width / 220f);
        Text("ВАШЕ ВЛИЯНИЕ", new Rectangle(p.X + 14, p.Y + 15, p.Width - 28, 24), Mint, .86f * s);
        Text($"Доступно: {_world.AvailableResonance} R  /  меняйте условия", new Rectangle(p.X + 14, p.Y + 42, p.Width - 28, 22), Muted, .63f * s);
        var titles = new[] { "01   Цветение", "02   Маяк", "03   Озарение", "04   Проход" };
        var descriptions = new[] { "Пища рядом; считаем, сколько агенты собрали.", "Усиливает тягу разведчиков к точке.", "Показывает группе реальный источник еды.", "Открыть стену; считаем проходы жителей." };
        for (var i = 0; i < 4; i++)
        {
            var card = ToolCard(i);
            var selected = (int)_selectedTool == i + 1;
            var tint = i == 0 ? Gold : i == 1 ? Mint : new Color(174, 159, 239);
            UIPrimitives.DrawPanel(_spriteBatch, _pixel, card, selected ? new Color(30, 53, 57) : new Color(20, 33, 41), selected ? tint : UITheme.PanelBorder);
            DrawRect(new Rectangle(card.X, card.Y, 3, card.Height), tint);
            if (p.Height < 350)
            {
                var cost = i == 3 ? "2 R" : "1 R";
                Text($"{titles[i]}   {cost}", new Rectangle(card.X + 8, card.Y + 1, card.Width - 14, card.Height - 2), tint, .65f * s);
                continue;
            }
            Text(titles[i], new Rectangle(card.X + 10, card.Y + 5, card.Width - 42, 24), tint, .87f * s);
            Text(i == 3 ? "2 R" : "1 R", new Rectangle(card.Right - 32, card.Y + 7, 29, 22), Muted, .65f * s);
            Paragraph(descriptions[i], new Rectangle(card.X + 10, card.Y + 32, card.Width - 20, card.Height - 34), .68f * s, Muted);
        }
        var y = ToolCard(3).Bottom + 16;
        if (y + 60 >= p.Bottom) return;
        Text("СЛЕДУЮЩИЙ ШАГ", new Rectangle(p.X + 14, y, p.Width - 28, 24), Gold, .78f * s);
        var instruction = !_onboarding.IsComplete ? GuideText() : _world.LowEnergyAgents > 0
            ? $"{_world.LowEnergyAgents} жителей теряют силы. Слой «Энергия» поможет найти их; разместите пищу поблизости."
            : _livingSettlements.Count == 0 ? "Помогите двум основателям вырастить сообщество. Поселение возникнет рядом со стенами и группой жителей."
            : "Найдите незаселённый район. Маяк привлечёт исследователей, а цветение поможет закрепиться.";
        var instructionBounds = new Rectangle(p.X + 14, y + 30, p.Width - 28, Math.Min(126, p.Bottom - y - 45));
        if (instructionBounds.Height < 100)
            instruction = !_onboarding.IsComplete ? _onboarding.CurrentStep switch
            {
                OnboardingStep.MoveCamera => "WASD — камера. Колесо — масштаб.",
                OnboardingStep.SelectFounder => "Нажмите на жителя в мире.",
                OnboardingStep.StartTime => "Нажмите «Старт» или Space.",
                OnboardingStep.ObserveFood => "Золотая метка: житель несёт еду.",
                OnboardingStep.UseIntervention => "Карточка → точка карты → Старт.",
                _ => "Нажмите событие в хронике."
            } : _world.LowEnergyAgents > 0 ? "Нужна пища. Откройте слой «Энергия»." : "Маяк зовёт жителей в новый район.";
        if (_world.Ecology.Phase != 0)
            instruction = _world.EcologyStatus + ". Контур на карте — зона риска. Цветение поддержит пищу; маяк поможет переселиться. Space — пауза.";
        Paragraph(instruction, instructionBounds, .77f * s, UITheme.PrimaryText);
        var milestoneY = y + 168;
        if (milestoneY + 85 < p.Bottom)
        {
            Text("ПУТЬ ЭКСПЕРИМЕНТА", new Rectangle(p.X + 14, milestoneY, p.Width - 28, 24), Muted, .7f * s);
            var labels = new[] { "10 жителей: начало", "50 жителей: колония", "200 жителей: цивилизация" };
            var targets = new[] { 10, 50, 200 };
            for (var i = 0; i < 3; i++)
                Text((_world.AlivePopulation >= targets[i] ? "[+] " : "[ ] ") + labels[i], new Rectangle(p.X + 14, milestoneY + 28 + i * 23, p.Width - 28, 22), _world.AlivePopulation >= targets[i] ? Mint : Muted, .67f * s);
        }
    }

    private string GuideText() => _onboarding.CurrentStep switch
    {
        OnboardingStep.MoveCamera => "Колесо приближает мир. WASD перемещает камеру. Мини-карта переносит в любой район.",
        OnboardingStep.SelectFounder => "Нажмите на светящийся квадрат: справа появятся его потребности, знания и цель.",
        OnboardingStep.StartTime => "Нажмите «Старт» или Space. Основатели начнут исследовать мир самостоятельно.",
        OnboardingStep.ObserveFood => "Понаблюдайте за сбором пищи. Золотая метка у жителя — он несёт еду на склад.",
        OnboardingStep.UseIntervention => "Выберите карточку, затем точку на карте. На паузе действие ждёт запуска времени.",
        OnboardingStep.OpenChronicle => "Нажмите событие внизу — камера покажет, где оно произошло.",
        _ => "Ваш эксперимент продолжается.",
    };

    private Rectangle FollowButton() => new(_layout.RightPanel.X + 14, _layout.RightPanel.Y + 54, _layout.RightPanel.Width - 28, 28);

    private void DrawInspectorDeck()
    {
        var p = _layout.RightPanel;
        var s = Math.Min(_uiScale, p.Width / 290f);
        for (var i = 0; i < 3; i++) Button(TabButton(i), new[] { "Житель", "Колонии", "Аналитика" }[i], _inspectorTab == i ? Mint : Muted, i == _inspectorTab);
        if (_inspectorTab == 1)
        {
            Text($"ЖИВЫЕ ПОСЕЛЕНИЯ  /  {_livingSettlements.Count}", new Rectangle(p.X + 14, p.Y + 56, p.Width - 28, 28), Gold, .78f * s);
            _inspectorScrollOffset = Math.Clamp(_inspectorScrollOffset, 0, Math.Max(0, _livingSettlements.Count - 1));
            var y = p.Y + 90;
            foreach (var colony in _livingSettlements.Skip(_inspectorScrollOffset))
            {
                if (y + 70 > p.Bottom) break;
                var card = new Rectangle(p.X + 12, y, p.Width - 24, 68);
                UIPrimitives.DrawPanel(_spriteBatch, _pixel, card, new Color(20, 33, 41), FactionTint(colony.FactionId) * .5f);
                Text($"{ColonyName(colony.Id)}  /  #{colony.Id}", new Rectangle(card.X + 9, y + 5, card.Width - 18, 24), FactionTint(colony.FactionId), .88f * s);
                Text($"{colony.Population} жителей · поколение {colony.Generation}", new Rectangle(card.X + 9, y + 29, card.Width - 18, 22), UITheme.PrimaryText, .76f * s);
                Text($"Еда {colony.FoodStored:N0} · ~{colony.FoodStored * 22L / Math.Max(1, colony.Population) / .06:0} тиков питания", new Rectangle(card.X + 9, y + 49, card.Width - 18, 18), Muted, .63f * s);
                y += 76;
            }
            if (_livingSettlements.Count == 0) Paragraph("Здесь появятся сообщества, когда рядом со стенами соберутся жители. Клик по колонии переносит камеру.", new Rectangle(p.X + 14, y, p.Width - 28, 160), .82f * s, Muted);
            return;
        }
        if (_inspectorTab == 2) { DrawWorldAnalytics(p, s); return; }
        if (_inspectedAgent is not { } a)
        {
            Text("У КАЖДОГО — СВОЙ ПУТЬ", new Rectangle(p.X + 14, p.Y + 66, p.Width - 28, 28), Gold, .83f * s);
            Paragraph("Выберите жителя на карте. Вы увидите, куда он идёт, что помнит и чему научился.", new Rectangle(p.X + 14, p.Y + 106, p.Width - 28, 125), .86f * s, UITheme.PrimaryText);
            DrawPopulationChart(new Rectangle(p.X + 16, p.Y + 264, p.Width - 32, Math.Max(1, Math.Min(95, p.Height - 290))));
            return;
        }
        Button(FollowButton(), _followAgent ? "Следование включено" : "Следовать за жителем", _followAgent ? Mint : Muted, _followAgent);
        var y0 = p.Y + 97;
        Text($"ЖИТЕЛЬ #{a.Id:0000}", new Rectangle(p.X + 14, y0, p.Width - 28, 30), FactionTint(a.FactionId), 1.08f * s);
        Text($"{RoleName(a.Role)} · поколение {a.Generation} · F{a.FactionId}", new Rectangle(p.X + 14, y0 + 32, p.Width - 28, 24), Muted, .77f * s);
        Bar(new Rectangle(p.X + 14, y0 + 65, p.Width - 28, 6), a.Energy / 100, a.Energy < 30 ? UITheme.CriticalText : Mint);
        Text($"Энергия {a.Energy:0}%    Возраст {a.Age / Math.Max(1, a.MaxAge):P0}", new Rectangle(p.X + 14, y0 + 76, p.Width - 28, 22), Muted, .72f * s);
        var lines = new List<string>
        {
            a.Alive ? "Сейчас: " + ActionName(a.Action) : "Жизнь завершена",
            $"Цель: {a.TargetCell.X}, {a.TargetCell.Y} · несёт {a.CarriedFood} еды",
            "МОТИВЫ",
            a.Energy < 30 ? "Мало сил: нужно поесть или отдохнуть." : a.CarriedFood > 0 ? "Возвращает собранную пищу к стенам." : a.HasKnownFood ? "Помнит, где раньше находил пищу." : "Исследует доступные клетки вокруг.",
            "ЗНАНИЯ И ОПЫТ",
            $"Интеллект {a.Intelligence:P0} · обучение {a.LearningRate:P0}",
            $"Пища {a.FoodKnowledge:P0} · маршруты {a.RouteKnowledge:P0}",
            $"Доставок еды: {a.SuccessfulFoodTrips} · неудач: {a.FailedFoodTrips}",
            $"Уроков: {a.LearningUpdates} (+{a.PositiveOutcomes} / -{a.NegativeOutcomes})",
            "ОБЩЕНИЕ",
            $"Криков: {a.ShoutsMade} · услышано: {a.ShoutsHeard}",
            a.LastHeardShoutTick < 0 ? "Пока не слышал сигналов." : $"Последний сигнал: {SignalName(a.LastHeardShoutType)} от #{a.LastHeardShoutSenderId}",
            $"Доверие: пища {a.ShoutFoodTrust:P0}, опасность {a.ShoutDangerTrust:P0}",
            $"В памяти: {a.ShoutMemories?.Count ?? 0} мест · {a.ShoutReputations?.Count ?? 0} источников",
        };
        var content = new Rectangle(p.X + 14, y0 + 110, p.Width - 28, Math.Max(1, p.Bottom - y0 - 139));
        DrawScrollableText(lines, content, .83f * s);
        Text("Колесо — подробности · F — найти", new Rectangle(p.X + 14, p.Bottom - 25, p.Width - 28, 22), Muted, .66f * s);
    }

    private void DrawWorldAnalytics(Rectangle p, float s)
    {
        var y = p.Y + 62;
        Text("ЗДОРОВЬЕ ЭКСПЕРИМЕНТА", new Rectangle(p.X + 14, y, p.Width - 28, 28), Gold, .83f * s);
        DrawPopulationChart(new Rectangle(p.X + 14, y + 38, p.Width - 28, 76));
        y += 130;
        for (var i = 0; i < _uiCache.ScoreRows.Length; i++)
        {
            Text(_uiCache.ScoreRows[i], new Rectangle(p.X + 14, y, p.Width - 28, 24), Muted, .77f * s);
            Bar(new Rectangle(p.X + 14, y + 25, p.Width - 28, 4), _uiCache.ScoreValues[i] / 100f, Mint);
            y += 41;
        }
        Text("ДЕЙСТВИЯ → РЕЗУЛЬТАТ", new Rectangle(p.X + 14, y + 1, p.Width - 28, 22), Gold, .76f * s);
        y += 22;
        Text("Score экспериментальный · не лидерборд", new Rectangle(p.X + 14, y, p.Width - 28, 21), Muted, .65f * s);
        y += 23;
        DrawCachedAnalyticsText(new Rectangle(p.X + 14, y + 6, p.Width - 28, Math.Max(1, p.Bottom - y - 14)), .76f * s);
    }

    private void DrawCachedAnalyticsText(Rectangle bounds, float scale)
    {
        var width = Math.Max(1, bounds.Width - 8);
        if (_analyticsWrappedRevision != _uiCache.AnalyticsRevision ||
            _analyticsWrappedWidth != width || Math.Abs(_analyticsWrappedScale - scale) > .001f)
        {
            _analyticsWrappedLines.Clear();
            foreach (var paragraph in _uiCache.AnalyticsLines)
            {
                var wrapped = Wrap(paragraph, width, scale);
                for (var i = 0; i < wrapped.Count; i++)
                    _analyticsWrappedLines.Add(wrapped[i]);
            }
            _analyticsWrappedRevision = _uiCache.AnalyticsRevision;
            _analyticsWrappedWidth = width;
            _analyticsWrappedScale = scale;
        }

        var lineHeight = Math.Max(16, (int)Math.Ceiling(_uiFont.LineSpacing * scale) + 3);
        var capacity = Math.Max(0, bounds.Height / lineHeight);
        if (capacity == 0) return;
        _inspectorScrollOffset = Math.Clamp(_inspectorScrollOffset, 0, Math.Max(0, _analyticsWrappedLines.Count - capacity));
        for (var i = 0; i < capacity && i + _inspectorScrollOffset < _analyticsWrappedLines.Count; i++)
            Text(_analyticsWrappedLines[i + _inspectorScrollOffset],
                new Rectangle(bounds.X, bounds.Y + i * lineHeight, width, lineHeight), UITheme.PrimaryText, scale);
        if (_analyticsWrappedLines.Count > capacity)
        {
            DrawRect(new Rectangle(bounds.Right - 3, bounds.Y, 2, bounds.Height), UITheme.PanelBorder);
            DrawRect(new Rectangle(bounds.Right - 3,
                bounds.Y + bounds.Height * _inspectorScrollOffset / _analyticsWrappedLines.Count,
                2, Math.Max(6, bounds.Height * capacity / _analyticsWrappedLines.Count)), Mint);
        }
    }

    private void DrawArchive()
    {
        DrawMiniMap();
        var p = _layout.BottomPanel;
        var x = _layout.LeftPanel.Right + 14;
        Text("ХРОНИКА   /   клик — перейти · колесо — история", new Rectangle(x, p.Y + 6, Math.Max(1, ArchiveFilter().X - x - 8), 24), Gold, .73f * _uiScale);
        Button(ArchiveFilter(), _showAllEvents ? "Все события" : "Важное", Mint, !_showAllEvents);
        _chronicleScroll = Math.Clamp(_chronicleScroll, 0, Math.Max(0, _chronicleRows.Count - 2));
        for (var i = 0; i < 2; i++)
        {
            var index = i + _chronicleScroll;
            if (index >= _chronicleRows.Count) break;
            var entry = _chronicleRows[index];
            var row = ChronicleRow(i);
            if (row.Contains(Mouse.GetState().Position)) DrawRect(row, UITheme.ButtonHover);
            DrawRect(new Rectangle(row.X, row.Y + 7, 3, Math.Max(1, row.Height - 14)), entry.Event.Importance == WorldEventImportance.Critical ? UITheme.CriticalText : Mint);
            Text($"{entry.Event.Tick:N0}   {entry.Event.Description}" + (entry.Count > 1 ? $"   [ещё {entry.Count - 1}]" : ""), new Rectangle(row.X + 12, row.Y + 6, row.Width - 20, row.Height - 8), UITheme.PrimaryText, .77f * _uiScale);
        }
        if (_chronicleRows.Count == 0) Text("Здесь останутся первые открытия, рождения и истории ваших колоний.", ChronicleRow(0), Muted, .82f);
    }

    private void DrawMiniMap()
    {
        var r = MiniMapBounds();
        DrawRect(r, Ink);
        foreach (var wall in _world.Map.WallCells) MiniDot(wall, new Color(64, 83, 93));
        foreach (var a in _world.Agents) if (a.Alive) MiniDot(a.Cell, FactionTint(a.FactionId));
        var topLeft = _camera.ScreenToWorld(Vector2.Zero) / 8;
        var bottomRight = _camera.ScreenToWorld(new Vector2(_layout.WorldViewport.Width, _layout.WorldViewport.Height)) / 8;
        var view = Rectangle.Intersect(r, new Rectangle(r.X + (int)(topLeft.X * r.Width / 128), r.Y + (int)(topLeft.Y * r.Height / 80), Math.Max(1, (int)((bottomRight.X - topLeft.X) * r.Width / 128)), Math.Max(1, (int)((bottomRight.Y - topLeft.Y) * r.Height / 80))));
        if (view.Width > 0 && view.Height > 0) DrawRect(view, Mint, 1);
        void MiniDot(Point cell, Color color) => DrawRect(new Rectangle(r.X + cell.X * r.Width / 128, r.Y + cell.Y * r.Height / 80, 2, 2), color);
    }

    private void DrawAtmosphere(Rectangle visible, WorldRenderProfile renderProfile)
    {
        var atmosphereSpan = renderProfile.AtmosphereCellSpan;
        for (var y = visible.Top / atmosphereSpan * atmosphereSpan; y < visible.Bottom; y += atmosphereSpan)
        for (var x = visible.Left / atmosphereSpan * atmosphereSpan; x < visible.Right; x += atmosphereSpan)
        {
            var noise = (int)(unchecked((uint)(x * 73856093 ^ y * 19349663 ^ _world.Seed)) % 6);
            DrawRect(new Rectangle(x * 8, y * 8, atmosphereSpan * 8, atmosphereSpan * 8), new Color(13 + noise, 24 + noise, 30 + noise));
        }
        if (_mapLens == 0) return;
        for (var i = 0; i < _density.Length; i++)
        {
            if (_density[i] == 0) continue;
            var color = _mapLens == 2 ? Color.Lerp(UITheme.CriticalText, Mint, Math.Clamp(_energyDensity[i] / _density[i] / 100, 0, 1))
                : _mapLens == 3 ? FactionTint(_factionDensity[i] >= 0 ? 0 : 1) : Gold;
            if (_mapLens == 1) continue;
            DrawRect(new Rectangle(i % 16 * 64, i / 16 * 64, 64, 64), color * Math.Min(.48f, .12f + _density[i] * .03f));
        }
        if (_mapLens == 1)
        {
            if (!renderProfile.ShouldGlowResources(_mapLens, _camera.Zoom)) return;
            foreach (var food in _world.Food) if (food.Amount > 0 && visible.Contains(food.Cell)) DrawGlow(_camera.CellToWorld(food.Cell), 36, Gold * .8f);
            foreach (var pile in _world.FoodStorage) if (pile.Value > 0 && visible.Contains(pile.Key)) DrawGlow(_camera.CellToWorld(pile.Key), 25, Mint * .55f);
        }
    }

    private void DrawWorldAnnotations(Rectangle visible)
    {
        if (_world.Ecology.Phase != 0)
        {
            var center = _camera.CellToWorld(_world.DroughtCenter);
            var droughtRadius = _world.Ecology.Radius * 8;
            var droughtColor = _world.Ecology.Phase == 1 ? Gold : UITheme.CriticalText;
            var north = center + new Vector2(0, -droughtRadius); var east = center + new Vector2(droughtRadius, 0);
            var south = center + new Vector2(0, droughtRadius); var west = center + new Vector2(-droughtRadius, 0);
            Line(north, east, droughtColor, 1 / _camera.Zoom); Line(east, south, droughtColor, 1 / _camera.Zoom);
            Line(south, west, droughtColor, 1 / _camera.Zoom); Line(west, north, droughtColor, 1 / _camera.Zoom);
        }
        if (_inspectedAgent is { Alive: true } a)
        {
            var from = _camera.CellToWorld(a.Cell);
            for (var i = a.PathIndex; i < Math.Min(a.Path.Count, a.PathIndex + 24); i++)
            {
                var to = _camera.CellToWorld(a.Path[i]);
                Line(from, to, Mint * .5f, .65f);
                from = to;
            }
            if (a.HasKnownFood) Ring(_camera.CellToWorld(a.KnownFoodCell), 6, Gold * .8f);
            if (a.LastHeardShoutTick >= 0 && _world.Tick - a.LastHeardShoutTick < 250)
                Line(_camera.CellToWorld(a.Cell), _camera.CellToWorld(a.LastHeardShoutCell), new Color(169, 150, 233) * .45f, .6f);
        }
        foreach (var s in _livingSettlements.Take(24))
        {
            if (!visible.Contains(s.CenterCell)) continue;
            var center = _camera.CellToWorld(s.CenterCell);
            Ring(center, 10, FactionTint(s.FactionId) * .6f);
            if (_camera.Zoom > .65f)
                _spriteBatch.DrawString(_uiFont, ColonyName(s.Id), center + new Vector2(13, -8), FactionTint(s.FactionId), 0, Vector2.Zero, .48f / MathF.Sqrt(_camera.Zoom), SpriteEffects.None, 0);
        }
        if (_selectedTool == EmergentSimulationWorld.InterventionType.None || _menuOpen) return;
        var mouse = Mouse.GetState().Position;
        if (!_layout.WorldViewport.Contains(mouse) || Enumerable.Range(0, 4).Any(i => LensButton(i).Contains(mouse))) return;
        if (!_camera.TryScreenToCell(new Vector2(mouse.X - _layout.WorldViewport.X, mouse.Y - _layout.WorldViewport.Y), out var cell)) return;
        var radius = _selectedTool == EmergentSimulationWorld.InterventionType.InsightPulse ? 5 : _selectedTool == EmergentSimulationWorld.InterventionType.Beacon ? 30 : 2;
        var passage = _selectedTool == EmergentSimulationWorld.InterventionType.Passage;
        if (passage) radius = 0;
        var valid = passage ? _world.Map[cell] == CellType.Wall : _world.Map.IsWalkable(cell);
        var color = _world.AvailableResonance < (passage ? 2 : 1) || !valid ? UITheme.CriticalText : Mint;
        Ring(_camera.CellToWorld(cell), radius * 8, color * .55f);
        DrawRect(new Rectangle(cell.X * 8, cell.Y * 8, 8, 8), color, 1);
    }

    private void DrawGlow(Vector2 center, float radius, Color color)
    {
        if (_glowTexture is null) return;
        _spriteBatch.Draw(_glowTexture, new Rectangle((int)(center.X - radius), (int)(center.Y - radius), (int)(radius * 2), (int)(radius * 2)), color);
    }

    private void Ring(Vector2 center, float radius, Color color)
    {
        var from = center + new Vector2(radius, 0);
        for (var i = 1; i <= 48; i++)
        {
            var angle = i * MathF.Tau / 48;
            var to = center + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * radius;
            Line(from, to, color, Math.Max(.5f, 1 / _camera.Zoom));
            from = to;
        }
    }

    private void Line(Vector2 from, Vector2 to, Color color, float thickness = 1f)
    {
        var delta = to - from;
        _spriteBatch.Draw(_pixel, from, null, color, MathF.Atan2(delta.Y, delta.X), Vector2.Zero, new Vector2(delta.Length(), thickness), SpriteEffects.None, 0);
    }

    private void DrawPopulationChart(Rectangle r)
    {
        if (r.Width <= 0 || r.Height < 30) return;
        DrawRect(r, Ink);
        Text($"НАСЕЛЕНИЕ   {_world.AlivePopulation}", new Rectangle(r.X + 8, r.Y + 3, r.Width - 16, 23), Muted, .67f);
        if (_populationHistory.Count < 2) return;
        var peak = Math.Max(1, _populationHistory.Max());
        var i = 0;
        Vector2? previous = null;
        foreach (var value in _populationHistory)
        {
            var point = new Vector2(r.X + 8 + (r.Width - 16) * i++ / (float)(_populationHistory.Count - 1), r.Bottom - 7 - (r.Height - 34) * value / (float)peak);
            if (previous.HasValue) Line(previous.Value, point, Mint, 2);
            previous = point;
        }
    }

    private void DrawExperienceDebug()
    {
        var vp = _layout.WorldViewport;
        _debugBounds = new Rectangle(vp.X + 10, vp.Y + 47, Math.Min(380, vp.Width - 20), Math.Min(270, vp.Height - 57));
        if (_debugBounds.Height < 30) return;
        UIPrimitives.DrawPanel(_spriteBatch, _pixel, _debugBounds, Ink, Gold);
        var profile = _world.StepPerformance;
        Clip(_debugBounds, () => Paragraph($"DIAGNOSTICS / F3\nFPS {_displayFps:0}   Tick {_world.Tick:N0}\nActive {_world.ActiveAgentCount} / dormant {_world.DormantAgentCount}\nProxy coverage {_world.AggregatedAgentCount}\nRender: world {StopwatchTicksToMilliseconds(_lastWorldDrawTicks):0.00} / UI {StopwatchTicksToMilliseconds(_lastUIDrawTicks):0.00} ms\nTick p50/p95 {profile.Total.P50Milliseconds:0.##}/{profile.Total.P95Milliseconds:0.##} ms ({profile.Total.Samples} recent samples)\nTick p95: agent loop (+pathfinding) {profile.AgentLoop.P95Milliseconds:0.##} / distance-grid prep {profile.DistanceGridPreparation.P95Milliseconds:0.##} / other (+LOD) {profile.OtherWork.P95Milliseconds:0.##} ms\nBacklog {_world.BacklogSeconds:0.00}s\nSeed {_world.Seed}\nСигналы {_world.ShoutsMade} / услышано {_world.ShoutsHeard}\nЖивых колоний {_livingSettlements.Count}\nF10 — папка аналитики", Inset(_debugBounds, 10), .72f, Muted));
    }

    private Rectangle MenuBounds() => new(Math.Max(8, (Window.ClientBounds.Width - 600) / 2), Math.Max(8, (Window.ClientBounds.Height - 550) / 2), Math.Min(600, Window.ClientBounds.Width - 16), Math.Min(550, Window.ClientBounds.Height - 16));
    private Rectangle MenuButton(int i)
    {
        var r = MenuBounds();
        var compact = r.Height < 430;
        var h = compact ? 28 : 40;
        return new Rectangle(r.X + 22 + i % 2 * ((r.Width - 44) / 2), r.Y + (compact ? 65 : 97) + i / 2 * (h + (compact ? 4 : 8)), (r.Width - 54) / 2, h);
    }

    private void DrawExperienceMenu()
    {
        DrawRect(_layout.WindowBounds, Color.Black * .8f);
        var r = MenuBounds();
        UIPrimitives.DrawPanel(_spriteBatch, _pixel, r, UITheme.PanelBg, Mint * .5f);
        Clip(r, () =>
        {
            Text("HOLLOWBOUND", new Rectangle(r.X + 24, r.Y + 18, r.Width - 48, 36), Mint, 1.25f);
            if (r.Height >= 430) Text("THE LIVING ARCHIVE   /   мир ждёт вашего решения", new Rectangle(r.X + 24, r.Y + 57, r.Width - 48, 28), Muted, .78f);
            var labels = new[] { "Вернуться к миру", "Сохранить мир", "Загрузить сохранение", _confirmNewWorld ? "Подтвердить новый мир" : "Новый эксперимент", "Полный экран / окно", $"Интерфейс: {_uiScale:P0}", "Открыть логи", "Скриншот · F12", "Сохранить и выйти", "Повторить обучение" };
            for (var i = 0; i < labels.Length; i++) Button(MenuButton(i), labels[i], i == 3 && _confirmNewWorld ? UITheme.CriticalText : i == 0 ? Mint : Muted);
            var y = MenuButton(9).Bottom + 14;
            Paragraph(_confirmNewWorld ? "Текущий мир будет сохранён перед созданием нового. Новый эксперимент начнётся на паузе." : "Space — время · WASD — камера · колесо — масштаб\n1/2/3/4 — вмешательство · ПКМ — отменить инструмент\nF — житель · H — колония · G — событие · M — слой карты\nEsc — меню · F3 — диагностика · Ctrl +/- — размер текста\n\nЗолотой — пища. Мятный и янтарный — фракции.\nРешения и истории рождаются из жизни самих обитателей.", new Rectangle(r.X + 24, y, r.Width - 48, r.Bottom - y - 15), .78f, UITheme.PrimaryText);
        });
    }

    private void HandleMenuClick(Point p)
    {
        for (var i = 0; i < 10; i++)
        {
            if (!MenuButton(i).Contains(p)) continue;
            switch (i)
            {
                case 0: _menuOpen = false; _confirmNewWorld = false; break;
                case 1: SaveWorld(false); _menuOpen = false; break;
                case 2: LoadWorld(); RefreshExperience(); _menuOpen = false; break;
                case 3:
                    if (!_confirmNewWorld) { _confirmNewWorld = true; break; }
                    if (!ArchiveCurrentWorld()) { _menuOpen = false; break; }
                    ResetWorld(); RefreshExperience(); _camera.FitWorld(); _confirmNewWorld = false; _menuOpen = false; break;
                case 4: ToggleFullscreen(); break;
                case 5: SetUIScale(_uiScale >= 1.5f ? .75f : _uiScale + .25f); break;
                case 6: OpenLogsFolder(); break;
                case 7: _screenshotRequested = true; _menuOpen = false; break;
                case 8: SaveWorld(false); if (_saveStatus != "SAVE ERROR") Exit(); break;
                case 9: _onboarding.Reset(); _onboardingCompletionPersisted = false; ResetOnboardingBaselines(); SaveUISettings(); _menuOpen = false; break;
            }
            return;
        }
    }

    private void Button(Rectangle r, string text, Color color, bool active = false)
    {
        if (r.Width <= 0 || r.Height <= 0) return;
        var hover = r.Contains(Mouse.GetState().Position);
        UIPrimitives.DrawPanel(_spriteBatch, _pixel, r, active ? new Color(35, 66, 67) : hover ? new Color(35, 50, 60) : new Color(20, 32, 41), active ? color : UITheme.PanelBorder);
        Text(text, Inset(r, 5), color, Math.Min(.81f * _uiScale, .9f), true);
    }

    private bool ArchiveCurrentWorld()
    {
        try
        {
            var directory = Path.Combine(Path.GetDirectoryName(SessionSavePath)!, _isolatedPlaytest ? "hollowbound-playtest-archive" : "archive");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"world-{_world.Seed}-{DateTime.UtcNow:yyyyMMdd-HHmmssfff}.json");
            WorldSaveService.Save(_world, path);
            _analytics.LogEvent("world_archived", _world, _timeScale, _paused, path);
            return true;
        }
        catch (Exception ex)
        {
            _toolFeedback = "Архив не сохранён. Текущий мир остаётся открыт.";
            _toolFeedbackTimer = 5;
            _analytics.LogEvent("archive_failed", _world, _timeScale, _paused, ex.GetType().Name);
            return false;
        }
    }

    private void Text(string text, Rectangle r, Color color, float scale, bool center = false)
    {
        if (r.Width <= 0 || r.Height <= 0) return;
        // Final bounds guarantee: reduce only for unusually short controls.
        scale = Math.Min(scale, r.Height / (float)_uiFont.LineSpacing);
        UIPrimitives.DrawTextClipped(_spriteBatch, _uiFont, text, new Vector2(r.X, r.Y), color, r.Width, scale, center ? UIPrimitives.TextAlign.Center : UIPrimitives.TextAlign.Left);
    }

    private List<string> Wrap(string text, int width, float scale)
    {
        var key = (text, width, scale);
        if (_wrapCache.TryGetValue(key, out var cached)) return cached;
        if (_wrapCache.Count >= 512) _wrapCache.Clear();
        var lines = new List<string>();
        foreach (var paragraph in text.Split('\n'))
        {
            var line = "";
            foreach (var word in paragraph.Split(' '))
            {
                var next = line.Length == 0 ? word : line + " " + word;
                if (line.Length > 0 && _uiFont.MeasureString(next).X * scale > width) { lines.Add(line); line = word; }
                else line = next;
            }
            lines.Add(line);
        }
        _wrapCache[key] = lines;
        return lines;
    }

    private void Paragraph(string text, Rectangle r, float scale, Color color)
    {
        var h = Math.Max(14, (int)Math.Ceiling(_uiFont.LineSpacing * scale));
        var y = r.Y;
        foreach (var line in Wrap(text, r.Width, scale))
        {
            if (y + h > r.Bottom) break;
            Text(line, new Rectangle(r.X, y, r.Width, h), color, scale);
            y += h;
        }
    }

    private void DrawScrollableText(IEnumerable<string> paragraphs, Rectangle r, float scale)
    {
        var lines = paragraphs.SelectMany(p => Wrap(p, r.Width - 7, scale)).ToList();
        var h = Math.Max(16, (int)Math.Ceiling(_uiFont.LineSpacing * scale) + 3);
        var capacity = Math.Max(0, r.Height / h);
        if (capacity == 0) return;
        _inspectorScrollOffset = Math.Clamp(_inspectorScrollOffset, 0, Math.Max(0, lines.Count - capacity));
        for (var i = 0; i < capacity && i + _inspectorScrollOffset < lines.Count; i++)
            Text(lines[i + _inspectorScrollOffset], new Rectangle(r.X, r.Y + i * h, r.Width - 8, h), UITheme.PrimaryText, scale);
        if (lines.Count > capacity)
        {
            DrawRect(new Rectangle(r.Right - 3, r.Y, 2, r.Height), UITheme.PanelBorder);
            DrawRect(new Rectangle(r.Right - 3, r.Y + r.Height * _inspectorScrollOffset / lines.Count, 2, Math.Max(6, r.Height * capacity / lines.Count)), Mint);
        }
    }

    private void Clip(Rectangle r, Action draw)
    {
        var clip = Rectangle.Intersect(r, GraphicsDevice.Viewport.Bounds);
        if (clip.Width <= 0 || clip.Height <= 0) return;
        _spriteBatch.End();
        GraphicsDevice.ScissorRectangle = clip;
        _spriteBatch.Begin(samplerState: SamplerState.LinearClamp, rasterizerState: _scissorRasterizerState);
        draw();
        _spriteBatch.End();
        _spriteBatch.Begin(samplerState: SamplerState.LinearClamp);
    }

    private void Bar(Rectangle r, float value, Color color)
    {
        DrawRect(r, new Color(34, 47, 55));
        DrawRect(new Rectangle(r.X, r.Y, Math.Max(0, (int)(r.Width * Math.Clamp(value, 0, 1))), r.Height), color);
    }

    private static Rectangle Inset(Rectangle r, int amount) => new(r.X + amount, r.Y + amount, Math.Max(1, r.Width - amount * 2), Math.Max(1, r.Height - amount * 2));
    private Rectangle ArchiveFilter() => new(_layout.BottomPanel.Right - 132, _layout.BottomPanel.Y + 3, 116, 24);
    private static Color FactionTint(int id) => (id % 4) switch { 0 => Mint, 1 => Gold, 2 => new Color(173, 154, 233), _ => new Color(116, 176, 233) };
    private static string ColonyName(int id) => new[] { "Тихая бухта", "Узел света", "Глубокий сад", "Медный предел", "Новый исток", "Северная нить", "Тёплый разлом", "Дальний свод" }[Math.Abs(id % 8)];
    private static string RoleName(AgentRole role) => role switch { AgentRole.Forager => "Собиратель", AgentRole.Builder => "Строитель", AgentRole.Scout => "Разведчик", AgentRole.Keeper => "Хранитель", AgentRole.Pathfinder => "Следопыт", _ => "Универсал" };
    private static string SignalName(ShoutType type) => type switch { ShoutType.Food => "пища", ShoutType.Danger => "опасность", _ => "сбор" };
    private static string ActionName(AgentAction action) => action switch { AgentAction.Building => "продолжает стену", AgentAction.Digging => "прокладывает проход", AgentAction.Exploring => "исследует мир", AgentAction.Migrating => "ищет новое место", AgentAction.Resting => "бережёт силы", AgentAction.ReturningToWall => "возвращается к стенам", AgentAction.CarryingFood => "переносит пищу", AgentAction.StoringFood => "пополняет склад", AgentAction.GoingToFood => "идёт за пищей", AgentAction.GatheringFood => "собирает пищу", AgentAction.SearchingFood => "ищет источник пищи", _ => "выбирает следующий шаг" };
}
