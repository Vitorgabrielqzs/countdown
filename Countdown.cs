// Countdown - contador regressivo no estilo "flip clock" (C# WPF; interface montada em código,
// só a barra de rolagem usa um trecho pequeno de XAML)
// - Mini pop-up flutuante, sempre por cima; pincel no canto troca o tema claro/escuro
// - Tela cheia: contador redesenhado no tamanho da tela + frases motivadoras flutuando
// - Vários eventos; configurações com tópicos, abertas no lugar da página atual
// - Tudo é salvo automaticamente, com backup; só uma cópia do app roda por vez
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Countdown
{
    // ---------------------------------------------------------------
    // Dados salvos no arquivo JSON
    // ---------------------------------------------------------------
    public class EventItem
    {
        public string Name { get; set; } = "";
        public DateTime Target { get; set; }                            // data/hora de destino (horário local)
    }

    public class AppConfig
    {
        public List<EventItem> Events { get; set; } = new List<EventItem>();
        public int ActiveEvent { get; set; }                            // posição do evento mostrado no contador
        public bool DarkMode { get; set; } = true;                      // tema escolhido (padrão: escuro)
        public bool Transparent { get; set; }                           // modo transparente do mini pop-up
        public bool ShowSeconds { get; set; }                           // mostra a plaquinha de segundos
        public bool Sound { get; set; } = true;                         // tique-taque ligado (só toca com os segundos)
        public int SoundIndex { get; set; }                             // qual som (posição na lista ClockSound.Tracks)
        public double Volume { get; set; } = 0.5;                       // volume do som (0 = mudo, 1 = máximo)
        public List<string> Phrases { get; set; } = new List<string>(); // frases motivadoras da tela cheia
        public double? PopupRight { get; set; }                         // posição do mini pop-up (borda direita)
        public double? PopupTop { get; set; }                           // posição do mini pop-up (topo)

        // Formato antigo (um evento só, direto na raiz do JSON): lido só para converter, não é mais gravado
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Name { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public DateTime? Target { get; set; }

        // O evento ativo (o que aparece no contador)
        [JsonIgnore]
        public EventItem Active => Events.Count > 0 ? Events[Math.Clamp(ActiveEvent, 0, Events.Count - 1)] : Fallback;
        static readonly EventItem Fallback = new EventItem { Name = "Sem evento", Target = DateTime.MinValue };
    }

    // ---------------------------------------------------------------
    // Leitura e gravação do JSON em %APPDATA%\Countdown\config.json.
    // Nada é apagado aqui: só você apaga eventos e frases, pelos botões do app.
    // ---------------------------------------------------------------
    static class Storage
    {
        static string Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Countdown");
        static string FilePath => Path.Combine(Folder, "config.json");
        static string BackupPath => Path.Combine(Folder, "config.backup.json");   // versão anterior, sempre guardada
        static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,   // acentos legíveis no arquivo
        };

        // Lê o arquivo principal; se ele estiver danificado, usa o backup
        public static AppConfig? Load()
        {
            var cfg = TryRead(FilePath) ?? TryRead(BackupPath);
            if (cfg == null && File.Exists(FilePath))
            {
                // Nenhum dos dois pôde ser lido: guarda uma cópia do arquivo antes de recomeçar (nunca apaga)
                try { File.Copy(FilePath, Path.Combine(Folder, $"config-ilegivel-{DateTime.Now:yyyyMMdd-HHmmss}.json"), true); }
                catch { }
            }
            return cfg;
        }

        static AppConfig? TryRead(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                var cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path));
                if (cfg == null) return null;
                cfg.Events ??= new List<EventItem>();
                cfg.Phrases ??= new List<string>();
                // Converte o formato antigo (um evento só) para a lista de eventos
                if (cfg.Events.Count == 0 && !string.IsNullOrWhiteSpace(cfg.Name))
                    cfg.Events.Add(new EventItem { Name = cfg.Name, Target = cfg.Target ?? DateTime.Now });
                cfg.Name = null;
                cfg.Target = null;
                if (cfg.Events.Count == 0) return null;
                cfg.ActiveEvent = Math.Clamp(cfg.ActiveEvent, 0, cfg.Events.Count - 1);
                return cfg;
            }
            catch
            {
                return null;
            }
        }

        // Grava num arquivo temporário e troca de uma vez: se o PC desligar no meio, nada se corrompe.
        // A versão anterior fica como backup.
        public static void Save(AppConfig cfg)
        {
            try
            {
                Directory.CreateDirectory(Folder);
                string temp = FilePath + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(cfg, Options));
                if (File.Exists(FilePath)) File.Replace(temp, FilePath, BackupPath);
                else File.Move(temp, FilePath);
            }
            catch
            {
                // disco cheio ou arquivo bloqueado: tenta de novo na próxima alteração, sem fechar o app
            }
        }
    }

    // ---------------------------------------------------------------
    // Iniciar com o Windows: um valor na chave "Run" do usuário atual no registro.
    // É o mesmo lugar que o Gerenciador de Tarefas mostra em "Aplicativos de inicialização".
    // ---------------------------------------------------------------
    static class Startup
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string ValueName = "Countdown";

        public static bool IsEnabled()
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string;
        }

        public static void Set(bool enabled)
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled) key.SetValue(ValueName, "\"" + Environment.ProcessPath + "\"");   // caminho do .exe atual
            else key.DeleteValue(ValueName, false);
        }
    }

    // ---------------------------------------------------------------
    // Data digitada no formato brasileiro (dd/mm/aaaa hh:mm)
    // ---------------------------------------------------------------
    static class DateInput
    {
        const string Format = "dd/MM/yyyy HH:mm";
        static readonly CultureInfo PtBr = new CultureInfo("pt-BR");

        public static string ToText(DateTime date) => date.ToString(Format, PtBr);

        public static bool TryParse(string text, out DateTime value) =>
            DateTime.TryParseExact(text.Trim(), new[] { Format, "dd/MM/yyyy H:mm", "dd/MM/yyyy" },
                PtBr, DateTimeStyles.AssumeLocal, out value);
    }

    // ---------------------------------------------------------------
    // Tique-taque do relógio: toca em loop o som escolhido, só com os segundos à mostra
    // e enquanto a contagem está rodando. Começa e recomeça junto com a virada dos segundos,
    // para ficar no compasso do contador. Os .mp3 ficam embutidos dentro do .exe.
    // ---------------------------------------------------------------
    static class ClockSound
    {
        // Sons disponíveis: nome curto (mostrado nas configurações) + arquivo embutido
        public static readonly (string Name, string File)[] Tracks =
        {
            ("Cozinha", "relogio-cozinha.mp3"),                 // relógio de cozinha de quartzo
            ("Cinema", "relogio-cinematografico.mp3"),          // tique-taque lento e cinematográfico
            ("Clássico", "relogio-classico.mp3"),               // relógio de parede tradicional
        };

        static MediaPlayer? player;   // player de mídia do próprio WPF (toca mp3)
        static int loaded = -1;       // índice do som carregado (-1 = nenhum)
        static bool started;          // já começou a tocar?
        static MediaPlayer? preview;  // amostra curta tocada nas configurações quando o som principal está parado
        static DispatcherTimer? previewStop;

        // Muda o volume na hora (do som principal e da amostra)
        public static void SetVolume(double volume)
        {
            if (player != null) player.Volume = volume;
            if (preview != null) preview.Volume = volume;
        }

        // Toca alguns segundos do som escolhido para você ouvir como fica
        // (só quando o tique-taque principal não está tocando)
        public static void Preview(AppConfig cfg)
        {
            if (player != null) return;   // o som principal está tocando (ou prestes a tocar): ele mesmo serve de amostra
            int index = Math.Clamp(cfg.SoundIndex, 0, Tracks.Length - 1);
            try
            {
                if (preview == null || preview.Source?.LocalPath != Extract(Tracks[index].File))
                {
                    preview?.Close();
                    preview = new MediaPlayer();
                    preview.Open(new Uri(Extract(Tracks[index].File)));
                    preview.Play();
                }
                preview.Volume = cfg.Volume;
                // Para 3 s depois da última mexida
                previewStop ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                previewStop.Tick -= StopPreview;
                previewStop.Tick += StopPreview;
                previewStop.Stop();
                previewStop.Start();
            }
            catch
            {
                // sem saída de áudio: segue sem amostra
            }
        }

        static void StopPreview(object? sender, EventArgs e)
        {
            previewStop?.Stop();
            preview?.Stop();
            preview?.Close();
            preview = null;
        }

        // Liga, troca ou desliga o som conforme a configuração
        public static void Apply(AppConfig cfg)
        {
            bool shouldPlay = cfg.Sound && cfg.ShowSeconds && cfg.Active.Target > DateTime.Now;
            if (!shouldPlay)
            {
                Stop();
                return;
            }
            int index = Math.Clamp(cfg.SoundIndex, 0, Tracks.Length - 1);
            if (index == loaded) return;   // já está com o som certo

            Stop();
            try
            {
                var p = new MediaPlayer();
                p.MediaEnded += (_, _) =>   // reserva: se a gravação acabar, recomeça
                {
                    p.Position = TimeSpan.Zero;
                    p.Play();
                };
                p.Open(new Uri(Extract(Tracks[index].File)));
                p.Volume = cfg.Volume;
                StopPreview(null, EventArgs.Empty);   // o som de verdade substitui a amostra
                player = p;
                loaded = index;
                started = false;

                // Normalmente começa na próxima virada dos segundos (OnSecond);
                // se nenhum contador estiver na tela, começa sozinho logo depois
                var fallback = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1300) };
                fallback.Tick += (_, _) =>
                {
                    fallback.Stop();
                    if (player == p && !started)
                    {
                        p.Play();
                        started = true;
                    }
                };
                fallback.Start();
            }
            catch
            {
                // sem saída de áudio ou arquivo indisponível: segue sem som
            }
        }

        // Chamado a cada virada da plaquinha de segundos
        public static void OnSecond()
        {
            var p = player;
            if (p == null) return;
            if (!started)
            {
                p.Position = TimeSpan.Zero;
                p.Play();
                started = true;
                return;
            }
            // Perto do fim da gravação: recomeça exatamente na virada, em vez de esperar acabar
            if (p.NaturalDuration.HasTimeSpan && p.Position >= p.NaturalDuration.TimeSpan - TimeSpan.FromSeconds(1))
                p.Position = TimeSpan.Zero;
        }

        static void Stop()
        {
            player?.Stop();
            player?.Close();
            player = null;
            loaded = -1;
            started = false;
        }

        // O player do Windows precisa de um arquivo de verdade: copia o som embutido
        // para %APPDATA%\Countdown\sounds (só se ainda não existir ou se tiver mudado)
        static string Extract(string file)
        {
            string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Countdown", "sounds");
            string path = Path.Combine(folder, file);
            using var source = typeof(ClockSound).Assembly.GetManifestResourceStream(file)
                ?? throw new FileNotFoundException(file);
            if (!File.Exists(path) || new FileInfo(path).Length != source.Length)
            {
                Directory.CreateDirectory(folder);
                using var target = File.Create(path);
                source.CopyTo(target);
            }
            return path;
        }
    }

    // ---------------------------------------------------------------
    // Tema: cores dos dois modos, fontes, ícones, animações e peças reaproveitadas.
    // Os elementos pegam as cores com SetResourceReference (o "DynamicResource" do XAML):
    // quando Apply troca os valores, tudo na tela se atualiza sozinho.
    // ---------------------------------------------------------------
    static class Theme
    {
        public static Color AccentColor { get; private set; }

        // Bahnschrift vem com o Windows 10/11; a variante Condensed lembra os números de flip clock
        public static readonly FontFamily Digits = new FontFamily("Bahnschrift SemiBold Condensed, Bahnschrift, Arial Narrow");
        public static readonly FontFamily Ui = new FontFamily("Bahnschrift, Segoe UI");
        // Ícones vetoriais do próprio Windows (Win11: Segoe Fluent Icons; Win10: Segoe MDL2 Assets)
        public static readonly FontFamily Icons = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");

        public const string GlyphGear = "", GlyphClose = "", GlyphFullScreen = "",
            GlyphBackToWindow = "", GlyphEdit = "", GlyphAdd = "", GlyphDelete = "",
            GlyphCalendar = "", GlyphPalette = "", GlyphVolume = "", GlyphSystem = "",
            GlyphBack = "", GlyphBrush = "", GlyphNext = "\uE76C";

        static readonly ControlTemplate InputTemplate = BuildInputTemplate();

        // Ícone das janelas (barra de tarefas e Alt+Tab), lido de dentro do próprio .exe.
        // Definido na janela, o Windows mostra este ícone na hora, sem depender do cache de ícones.
        public static readonly ImageSource? AppIcon = LoadAppIcon();

        static ImageSource? LoadAppIcon()
        {
            try
            {
                using var stream = typeof(Theme).Assembly.GetManifestResourceStream("Countdown.ico");
                if (stream == null) return null;
                // Decodificador de .ico: o WPF escolhe sozinho o tamanho certo (barra de tarefas, Alt+Tab...)
                var decoder = new IconBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                return decoder.Frames[0];
            }
            catch
            {
                return null;   // sem o recurso: a janela usa o ícone padrão do .exe
            }
        }

        // Barra de rolagem fina, nas cores do tema (a padrão do Windows é larga e cinza-clara)
        const string ScrollBarXaml = @"
<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='ScrollBar'>
  <Setter Property='Width' Value='6'/>
  <Setter Property='MinWidth' Value='6'/>
  <Setter Property='Template'>
    <Setter.Value>
      <ControlTemplate TargetType='ScrollBar'>
        <Track Name='PART_Track' IsDirectionReversed='True'>
          <Track.Thumb>
            <Thumb>
              <Thumb.Template>
                <ControlTemplate TargetType='Thumb'>
                  <Border CornerRadius='3' Background='{DynamicResource ScrollThumb}'/>
                </ControlTemplate>
              </Thumb.Template>
            </Thumb>
          </Track.Thumb>
        </Track>
      </ControlTemplate>
    </Setter.Value>
  </Setter>
</Style>";

        public static void Apply(bool dark)
        {
            var r = Application.Current.Resources;
            if (!r.Contains(typeof(ScrollBar))) r[typeof(ScrollBar)] = (Style)XamlReader.Parse(ScrollBarXaml);
            AccentColor = dark ? Rgb(0xF0, 0xA8, 0x30) : Rgb(0xC2, 0x48, 0x48);   // âmbar no escuro, vermelho no claro

            r["Accent"] = Solid(AccentColor);
            r["Title"] = Solid(dark ? Rgb(0xF5, 0xF5, 0xF5) : Rgb(0x22, 0x22, 0x22));
            r["Icon"] = Solid(dark ? Rgb(0x8C, 0x8C, 0x8C) : Rgb(0x8A, 0x8A, 0x8A));
            r["Label"] = Solid(dark ? Rgb(0x9A, 0x9A, 0x9A) : Rgb(0x7A, 0x7A, 0x7A));
            r["Error"] = Solid(dark ? Rgb(0xFF, 0x6B, 0x6B) : Rgb(0xC2, 0x48, 0x48));

            // Fundo: escuro com uma luz suave no centro-topo / claro com degradê vertical
            r["PanelBack"] = dark
                ? Frozen(new RadialGradientBrush(Rgb(0x30, 0x30, 0x30), Rgb(0x14, 0x14, 0x14))
                    { GradientOrigin = new Point(0.5, 0.3), Center = new Point(0.5, 0.3), RadiusX = 0.9, RadiusY = 1.1 })
                : Vertical(Rgb(0xFB, 0xFB, 0xFB), Rgb(0xE4, 0xE4, 0xE4));
            // Borda: clara em cima e escura embaixo, dá impressão de relevo
            r["PanelBorder"] = dark
                ? Vertical(Rgb(0x52, 0x52, 0x52), Rgb(0x1A, 0x1A, 0x1A))
                : Vertical(Colors.White, Rgb(0xC2, 0xC2, 0xC2));
            // Painel de frases da tela cheia
            r["EditorBack"] = Solid(dark ? Color.FromArgb(0xF0, 0x1A, 0x1A, 0x1A) : Color.FromArgb(0xF2, 0xFF, 0xFF, 0xFF));

            // Configurações: menu lateral, item selecionado/hover, linhas, cartões de evento, chaves, rolagem
            r["SidebarBack"] = Solid(dark ? Rgb(0x17, 0x17, 0x17) : Rgb(0xEC, 0xEC, 0xEC));
            r["NavSelected"] = Solid(dark ? Rgb(0x2E, 0x2E, 0x2E) : Rgb(0xDA, 0xDA, 0xDA));
            r["NavHover"] = Solid(dark ? Rgb(0x23, 0x23, 0x23) : Rgb(0xE3, 0xE3, 0xE3));
            r["RowLine"] = Solid(dark ? Rgb(0x2C, 0x2C, 0x2C) : Rgb(0xD9, 0xD9, 0xD9));
            r["CardRow"] = Solid(dark ? Rgb(0x1A, 0x1A, 0x1A) : Colors.White);
            r["SwitchOff"] = Solid(dark ? Rgb(0x26, 0x26, 0x26) : Rgb(0xE6, 0xE6, 0xE6));
            r["SwitchBorder"] = Solid(dark ? Rgb(0x5A, 0x5A, 0x5A) : Rgb(0xA8, 0xA8, 0xA8));
            r["SwitchThumbOn"] = Solid(dark ? Rgb(0x15, 0x15, 0x15) : Colors.White);
            r["SwitchThumbOff"] = Solid(dark ? Rgb(0xB0, 0xB0, 0xB0) : Rgb(0x6E, 0x6E, 0x6E));
            r["ScrollThumb"] = Solid(dark ? Rgb(0x50, 0x50, 0x50) : Rgb(0xB5, 0xB5, 0xB5));

            // Campos e botões
            r["InputBack"] = Solid(dark ? Rgb(0x1C, 0x1C, 0x1C) : Colors.White);
            r["InputFore"] = Solid(dark ? Rgb(0xEE, 0xEE, 0xEE) : Rgb(0x2B, 0x2B, 0x2B));
            r["InputBorder"] = Solid(dark ? Rgb(0x44, 0x44, 0x44) : Rgb(0xCF, 0xCF, 0xCF));
            r["ButtonBack"] = Solid(dark ? AccentColor : Rgb(0x1A, 0x1A, 0x1A));
            r["ButtonHover"] = Solid(dark ? Rgb(0xFF, 0xC1, 0x5A) : AccentColor);
            r["ButtonFore"] = Solid(dark ? Rgb(0x15, 0x15, 0x15) : Colors.White);

            // Plaquinhas: escuras nos dois temas, como num flip clock de verdade.
            // Metade de cima um pouco mais clara que a de baixo, com corte seco no meio.
            r["CardBack"] = Frozen(new LinearGradientBrush(new GradientStopCollection
            {
                new GradientStop(Rgb(0x40, 0x40, 0x40), 0.0),
                new GradientStop(Rgb(0x30, 0x30, 0x30), 0.5),
                new GradientStop(Rgb(0x24, 0x24, 0x24), 0.5),
                new GradientStop(Rgb(0x19, 0x19, 0x19), 1.0),
            }, 90));
            // Contorno da plaquinha: fio de luz no topo, sombra na base
            r["CardEdge"] = Frozen(new LinearGradientBrush(new GradientStopCollection
            {
                new GradientStop(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF), 0.0),
                new GradientStop(Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF), 0.2),
                new GradientStop(Color.FromArgb(0x00, 0x00, 0x00, 0x00), 0.5),
                new GradientStop(Color.FromArgb(0x70, 0x00, 0x00, 0x00), 1.0),
            }, 90));
            r["Digit"] = Vertical(Colors.White, Rgb(0xC4, 0xC4, 0xC4));          // números: branco descendo para cinza claro
            r["Split"] = Solid(Rgb(0x0A, 0x0A, 0x0A));                           // fenda do meio
            r["SplitLight"] = Solid(Color.FromArgb(0x1C, 0xFF, 0xFF, 0xFF));     // brilho logo abaixo da fenda
        }

        static Color Rgb(byte r, byte g, byte b) => Color.FromRgb(r, g, b);
        static Brush Solid(Color c) => Frozen(new SolidColorBrush(c));
        static Brush Vertical(Color top, Color bottom) => Frozen(new LinearGradientBrush(top, bottom, 90));
        static Brush Frozen(Brush b) { b.Freeze(); return b; }   // Freeze = imutável, mais leve

        // --- Animações de clique e de entrada ---

        // Ao clicar, o elemento "afunda" um pouco e volta com um leve quique; hover > 1 cresce ao passar o mouse
        public static T Pressable<T>(T element, double pressed = 0.92, double hover = 1) where T : FrameworkElement
        {
            var scale = new ScaleTransform();
            var rotate = new RotateTransform();   // usado pelo Wiggle
            var group = new TransformGroup();
            group.Children.Add(scale);
            group.Children.Add(rotate);
            element.RenderTransformOrigin = new Point(0.5, 0.5);
            element.RenderTransform = group;

            void To(double value, int ms, bool bounce)
            {
                IEasingFunction ease = bounce
                    ? new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.6 }
                    : new QuadraticEase { EasingMode = EasingMode.EaseOut };
                var animation = new DoubleAnimation(value, TimeSpan.FromMilliseconds(ms)) { EasingFunction = ease };
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, animation);
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, animation);
            }

            if (hover != 1) element.MouseEnter += (_, _) => To(hover, 150, false);
            element.MouseLeave += (_, _) => To(1, 180, false);
            element.PreviewMouseLeftButtonDown += (_, _) => To(pressed, 70, false);
            element.PreviewMouseLeftButtonUp += (_, _) => To(element.IsMouseOver ? hover : 1, 320, true);
            return element;
        }

        // Balançada rápida (ex.: pincel ao trocar o tema)
        public static void Wiggle(FrameworkElement element)
        {
            if (element.RenderTransform is not TransformGroup group || group.Children[1] is not RotateTransform rotate) return;
            var animation = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(450) };
            animation.KeyFrames.Add(new EasingDoubleKeyFrame(-18, KeyTime.FromPercent(0.25)));
            animation.KeyFrames.Add(new EasingDoubleKeyFrame(14, KeyTime.FromPercent(0.55)));
            animation.KeyFrames.Add(new EasingDoubleKeyFrame(-6, KeyTime.FromPercent(0.8)));
            animation.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromPercent(1)));
            rotate.BeginAnimation(RotateTransform.AngleProperty, animation);
        }

        // Entrada suave: desliza alguns pixels e aparece
        public static void SlideIn(UIElement element, double fromX = 0, double fromY = 8)
        {
            var move = new TranslateTransform(fromX, fromY);
            element.RenderTransform = move;
            var duration = TimeSpan.FromMilliseconds(240);
            var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
            move.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(fromX, 0, duration) { EasingFunction = ease });
            move.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(fromY, 0, duration) { EasingFunction = ease });
            element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, duration));
        }

        // "Pulinho" de destaque (ex.: evento que acabou de ser escolhido)
        public static void Pop(FrameworkElement element)
        {
            var scale = new ScaleTransform(0.94, 0.94);
            element.RenderTransformOrigin = new Point(0.5, 0.5);
            element.RenderTransform = scale;
            var animation = new DoubleAnimation(0.94, 1, TimeSpan.FromMilliseconds(380))
            {
                EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.6 },
            };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, animation);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, animation);
        }

        // --- Peças reaproveitadas ---

        // Texto pequeno mais nítido: alinha as letras aos pixels da tela
        public static TextBlock Crisp(TextBlock t)
        {
            TextOptions.SetTextFormattingMode(t, TextFormattingMode.Display);
            return t;
        }

        // Texto com cor do tema (atalho usado nas janelas)
        public static TextBlock Text(string text, double size, string colorKey, FontWeight? weight = null)
        {
            var t = Crisp(new TextBlock
            {
                Text = text,
                FontFamily = Ui,
                FontSize = size,
                FontWeight = weight ?? FontWeights.Normal,
                TextWrapping = TextWrapping.Wrap,
            });
            t.SetResourceReference(TextBlock.ForegroundProperty, colorKey);
            return t;
        }

        // Rótulo acima de um campo
        public static TextBlock Caption(string text)
        {
            var t = Text(text, 11, "Label");
            t.Margin = new Thickness(0, 8, 0, 3);
            return t;
        }

        // Painel com cantos arredondados, borda em relevo e sombra.
        // A sombra fica numa borda separada para não ser aplicada ao conteúdo (o que borraria o texto).
        public static Grid Panel(UIElement content)
        {
            var shadow = new Border
            {
                Background = Brushes.Black,
                CornerRadius = new CornerRadius(14),
                Margin = new Thickness(1),
                Effect = new DropShadowEffect { BlurRadius = 16, ShadowDepth = 3, Opacity = 0.45 },
            };
            var panel = new Border
            {
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(14),
                Child = content,
            };
            panel.SetResourceReference(Border.BackgroundProperty, "PanelBack");
            panel.SetResourceReference(Border.BorderBrushProperty, "PanelBorder");
            // Janela transparente desliga o ClearType; este aviso diz ao WPF que aqui o fundo é opaco,
            // então ele pode usar ClearType (texto mais definido)
            RenderOptions.SetClearTypeHint(panel, ClearTypeHint.Enabled);

            var grid = new Grid { Margin = new Thickness(14) };   // margem = espaço para a sombra aparecer
            grid.Children.Add(shadow);
            grid.Children.Add(panel);
            return grid;
        }

        // Ícone clicável: cinza; ganha a cor de destaque ao passar o mouse ou quando está "ligado".
        // Tag guarda a cor fixa do ícone ("Accent" = ligado, "Error" = confirmar apagar, null = normal)
        public static TextBlock Icon(string glyph, string tip, Action onClick, double size = 12)
        {
            var t = new TextBlock
            {
                Text = glyph,
                FontFamily = Icons,
                FontSize = size,
                Background = Brushes.Transparent,   // fundo transparente para a área toda ser clicável
                Cursor = Cursors.Hand,
                ToolTip = tip,
                Padding = new Thickness(Math.Round(size * 0.3)),
                VerticalAlignment = VerticalAlignment.Center,
            };
            t.SetResourceReference(TextBlock.ForegroundProperty, "Icon");
            t.MouseEnter += (_, _) => t.SetResourceReference(TextBlock.ForegroundProperty, t.Tag as string == "Error" ? "Error" : "Accent");
            t.MouseLeave += (_, _) => t.SetResourceReference(TextBlock.ForegroundProperty, t.Tag as string ?? "Icon");
            t.MouseLeftButtonDown += (_, e) => e.Handled = true;                 // impede que o clique vire "arrastar"
            t.MouseLeftButtonUp += (_, e) => { e.Handled = true; onClick(); };
            return Pressable(t, 0.8, 1.15);
        }

        // Marca um ícone como ligado/desligado (ligado = fica na cor de destaque)
        public static void SetActive(TextBlock icon, bool active)
        {
            icon.Tag = active ? "Accent" : null;
            if (!icon.IsMouseOver)
                icon.SetResourceReference(TextBlock.ForegroundProperty, active ? "Accent" : "Icon");
        }

        // Lixeira que apaga em dois cliques: o 1º "arma" (fica vermelha e aparece "apagar?"),
        // o 2º apaga. Se não clicar de novo em 3 s, desarma sozinha.
        public static StackPanel DeleteButton(Action onDelete, double size = 13)
        {
            var hint = Text("apagar?", 11, "Error");
            hint.VerticalAlignment = VerticalAlignment.Center;
            hint.Visibility = Visibility.Collapsed;
            var reset = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            TextBlock icon = null!;

            void Arm(bool on)
            {
                icon.Tag = on ? "Error" : null;
                icon.SetResourceReference(TextBlock.ForegroundProperty, on ? "Error" : "Icon");
                icon.ToolTip = on ? "Clique de novo para apagar" : "Apagar";
                hint.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
                reset.Stop();
                if (on)
                {
                    reset.Start();
                    Wiggle(icon);
                    SlideIn(hint, 6, 0);
                }
            }

            icon = Icon(GlyphDelete, "Apagar", () =>
            {
                if (icon.Tag as string == "Error")
                {
                    Arm(false);
                    onDelete();
                }
                else
                {
                    Arm(true);
                }
            }, size);
            reset.Tick += (_, _) => Arm(false);

            var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            panel.Children.Add(hint);
            panel.Children.Add(icon);
            return panel;
        }

        // Botão desenhado à mão (o botão padrão do WPF fica cinza no hover). compact = largura do texto
        public static Border Button(string text, Action onClick, bool compact = false)
        {
            var label = Crisp(new TextBlock
            {
                Text = text,
                FontFamily = Ui,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
            });
            label.SetResourceReference(TextBlock.ForegroundProperty, "ButtonFore");
            var b = new Border
            {
                CornerRadius = new CornerRadius(5),
                Padding = compact ? new Thickness(14, 6, 14, 6) : new Thickness(0, 7, 0, 7),
                HorizontalAlignment = compact ? HorizontalAlignment.Center : HorizontalAlignment.Stretch,
                Cursor = Cursors.Hand,
                Child = label,
            };
            b.SetResourceReference(Border.BackgroundProperty, "ButtonBack");
            b.MouseEnter += (_, _) => b.SetResourceReference(Border.BackgroundProperty, "ButtonHover");
            b.MouseLeave += (_, _) => b.SetResourceReference(Border.BackgroundProperty, "ButtonBack");
            b.MouseLeftButtonDown += (_, e) => e.Handled = true;
            b.MouseLeftButtonUp += (_, e) => { e.Handled = true; onClick(); };
            return Pressable(b, 0.95);
        }

        // Chave liga/desliga (estilo Windows 11): a bolinha desliza e o fundo ganha a cor de destaque
        public static Border Switch(bool initial, Action<bool> changed)
        {
            bool on = initial;
            var move = new TranslateTransform();
            var thumb = new Border
            {
                Width = 12,
                Height = 12,
                CornerRadius = new CornerRadius(6),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                RenderTransform = move,
            };
            var track = new Border
            {
                Width = 40,
                Height = 22,
                CornerRadius = new CornerRadius(11),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(4, 0, 4, 0),
                Cursor = Cursors.Hand,
                Child = thumb,
            };

            void Render(bool animate)
            {
                track.SetResourceReference(Border.BackgroundProperty, on ? "Accent" : "SwitchOff");
                track.SetResourceReference(Border.BorderBrushProperty, on ? "Accent" : "SwitchBorder");
                thumb.SetResourceReference(Border.BackgroundProperty, on ? "SwitchThumbOn" : "SwitchThumbOff");
                double x = on ? 18 : 0;   // 40 de largura - 2 de borda - 8 de espaço interno - 12 da bolinha
                if (animate)
                    move.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(x, TimeSpan.FromMilliseconds(220))
                    {
                        EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.5 },
                    });
                else
                    move.X = x;
            }

            Render(false);
            track.MouseLeftButtonDown += (_, e) => e.Handled = true;
            track.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                on = !on;
                Render(true);
                changed(on);
            };
            return Pressable(track, 0.9);
        }

        // Botões lado a lado onde só um fica escolhido (ex.: "Cozinha | Cinema")
        public static Border Segmented(string[] options, int selected, Action<int> changed)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            var items = new List<Border>();

            void Render()
            {
                for (int i = 0; i < items.Count; i++)
                {
                    bool on = i == selected;
                    if (on) items[i].SetResourceReference(Border.BackgroundProperty, "Accent");
                    else items[i].Background = Brushes.Transparent;
                    items[i].SetResourceReference(TextElement.ForegroundProperty, on ? "ButtonFore" : "InputFore");
                }
            }

            for (int i = 0; i < options.Length; i++)
            {
                int index = i;   // cópia para a função abaixo lembrar qual botão é
                var item = new Border
                {
                    CornerRadius = new CornerRadius(5),
                    Padding = new Thickness(12, 5, 12, 5),
                    Cursor = Cursors.Hand,
                    Child = Crisp(new TextBlock { Text = options[i], FontFamily = Ui, FontSize = 12, FontWeight = FontWeights.SemiBold }),
                };
                item.MouseLeftButtonDown += (_, e) => e.Handled = true;
                item.MouseLeftButtonUp += (_, e) =>
                {
                    e.Handled = true;
                    if (index == selected) return;
                    selected = index;
                    Render();
                    changed(index);
                };
                items.Add(Pressable(item, 0.9));
                row.Children.Add(item);
            }

            var outer = new Border { CornerRadius = new CornerRadius(7), BorderThickness = new Thickness(1), Padding = new Thickness(2), Child = row };
            outer.SetResourceReference(Border.BackgroundProperty, "InputBack");
            outer.SetResourceReference(Border.BorderBrushProperty, "InputBorder");
            Render();
            return outer;
        }

        // Barra deslizante (0 a 1): arraste a bolinha, clique na barra ou use a rodinha do mouse.
        // changing = enquanto mexe (ao vivo); changed = quando solta (hora de salvar)
        public static Grid Slider(double initial, Action<double> changing, Action<double> changed)
        {
            double value = Math.Clamp(initial, 0, 1);
            var track = new Border { Height = 4, CornerRadius = new CornerRadius(2), VerticalAlignment = VerticalAlignment.Center };
            track.SetResourceReference(Border.BackgroundProperty, "SwitchOff");
            var fill = new Border { Height = 4, CornerRadius = new CornerRadius(2), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
            fill.SetResourceReference(Border.BackgroundProperty, "Accent");
            var move = new TranslateTransform();
            var thumbScale = new ScaleTransform(1, 1);
            var thumbTransform = new TransformGroup();
            thumbTransform.Children.Add(thumbScale);
            thumbTransform.Children.Add(move);
            var thumb = new Border
            {
                Width = 14,
                Height = 14,
                CornerRadius = new CornerRadius(7),
                BorderThickness = new Thickness(2),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = thumbTransform,
            };
            thumb.SetResourceReference(Border.BackgroundProperty, "SwitchThumbOn");
            thumb.SetResourceReference(Border.BorderBrushProperty, "Accent");

            var area = new Grid { Height = 22, Background = Brushes.Transparent, Cursor = Cursors.Hand };   // fundo transparente = área clicável
            area.Children.Add(track);
            area.Children.Add(fill);
            area.Children.Add(thumb);

            void Render()
            {
                double usable = Math.Max(0, area.ActualWidth - thumb.Width);
                move.X = value * usable;
                fill.Width = value * usable + thumb.Width / 2;
            }

            void SetFrom(double x)
            {
                double usable = Math.Max(1, area.ActualWidth - thumb.Width);
                value = Math.Clamp((x - thumb.Width / 2) / usable, 0, 1);
                Render();
                changing(value);
            }

            // Bolinha cresce enquanto está sendo arrastada
            void Grow(bool on)
            {
                var a = new DoubleAnimation(on ? 1.3 : 1, TimeSpan.FromMilliseconds(on ? 90 : 260))
                {
                    EasingFunction = on ? new QuadraticEase { EasingMode = EasingMode.EaseOut } : new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.6 },
                };
                thumbScale.BeginAnimation(ScaleTransform.ScaleXProperty, a);
                thumbScale.BeginAnimation(ScaleTransform.ScaleYProperty, a);
            }

            area.SizeChanged += (_, _) => Render();
            area.MouseLeftButtonDown += (_, e) =>
            {
                e.Handled = true;            // não arrasta a janela
                area.CaptureMouse();         // continua recebendo o mouse mesmo saindo da barra
                Grow(true);
                SetFrom(e.GetPosition(area).X);
            };
            area.MouseMove += (_, e) =>
            {
                if (area.IsMouseCaptured) SetFrom(e.GetPosition(area).X);
            };
            area.MouseLeftButtonUp += (_, e) =>
            {
                if (!area.IsMouseCaptured) return;
                e.Handled = true;
                area.ReleaseMouseCapture();
                Grow(false);
                changed(value);
            };
            area.MouseWheel += (_, e) =>
            {
                e.Handled = true;
                value = Math.Clamp(value + (e.Delta > 0 ? 0.05 : -0.05), 0, 1);
                Render();
                changing(value);
                changed(value);
            };
            return area;
        }

        // Visual próprio para caixa de texto (o padrão do Windows fica azul e claro, destoando do tema escuro)
        static ControlTemplate BuildInputTemplate()
        {
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(5));
            border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
            border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
            border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
            border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
            border.AppendChild(new FrameworkElementFactory(typeof(ScrollViewer), "PART_ContentHost"));  // onde o texto é desenhado
            return new ControlTemplate(typeof(TextBox)) { VisualTree = border };
        }

        public static TextBox Input(string text, double fontSize = 13)
        {
            var box = new TextBox
            {
                Text = text,
                FontFamily = Ui,
                FontSize = fontSize,
                Padding = new Thickness(6, 4, 6, 4),
                BorderThickness = new Thickness(1),
                Template = InputTemplate,
            };
            box.SetResourceReference(Control.BackgroundProperty, "InputBack");
            box.SetResourceReference(Control.ForegroundProperty, "InputFore");
            box.SetResourceReference(Control.BorderBrushProperty, "InputBorder");
            box.SetResourceReference(TextBoxBase.CaretBrushProperty, "Accent");
            box.SetResourceReference(TextBoxBase.SelectionBrushProperty, "Accent");
            // Borda na cor de destaque enquanto o campo está selecionado
            box.GotKeyboardFocus += (_, _) => box.SetResourceReference(Control.BorderBrushProperty, "Accent");
            box.LostKeyboardFocus += (_, _) => box.SetResourceReference(Control.BorderBrushProperty, "InputBorder");
            return box;
        }

        // Janela base flutuante: sem barra de título, transparente, sempre por cima, arrastável
        public static void SetupFloating(Window w)
        {
            w.WindowStyle = WindowStyle.None;
            w.AllowsTransparency = true;
            w.Background = Brushes.Transparent;
            w.Icon = AppIcon;
            w.Topmost = true;
            w.ResizeMode = ResizeMode.NoResize;
            w.SizeToContent = SizeToContent.WidthAndHeight;   // a janela se ajusta ao conteúdo
            w.UseLayoutRounding = true;                        // posições em pixels inteiros = bordas e textos sem borrão
            w.SnapsToDevicePixels = true;
            w.MouseLeftButtonDown += (_, e) =>
            {
                if (e.ButtonState == MouseButtonState.Pressed) w.DragMove();   // arrastar com o mouse
            };
        }
    }

    // ---------------------------------------------------------------
    // Uma plaquinha do flip clock (2 dígitos, ou mais para dias > 99).
    // "s" é a escala: 1 no mini pop-up, maior na tela cheia. Tudo é desenhado
    // já no tamanho final (vetorial), sem ampliar imagem, então não perde nitidez.
    // Camadas: sombra, metades fixas, duas "abas" que dobram na troca, fenda e contorno.
    // ---------------------------------------------------------------
    class FlipCard : Grid
    {
        public int Digits { get; }
        readonly double w, h;
        readonly CornerRadius round;
        readonly TimeSpan halfFlip;
        readonly TextBlock topText, bottomText, flapTopText, flapBottomText;
        readonly Border flapTop, flapBottom, flapTopShade, flapBottomShade;
        readonly ScaleTransform flapTopScale, flapBottomScale;
        string value = "";

        public FlipCard(int digits, double s)
        {
            Digits = digits;
            h = 2 * Math.Round(32 * s);                    // altura par: o corte do meio cai num pixel inteiro
            w = Math.Round((digits * 26 + 14) * s);        // largura cresce com a quantidade de dígitos
            round = new CornerRadius(8 * s);
            halfFlip = TimeSpan.FromMilliseconds(150 * Math.Clamp(Math.Sqrt(s), 1, 2));   // plaquinha grande vira um pouco mais devagar
            Width = w;
            Height = h;

            // Sombra sob a plaquinha
            Children.Add(new Border
            {
                Background = Brushes.Black,
                CornerRadius = round,
                Margin = new Thickness(1),
                Effect = new DropShadowEffect { BlurRadius = 10 * s, ShadowDepth = 3 * s, Opacity = 0.5 },
            });

            Children.Add(Half(true, s, out topText, out _));        // fixa de cima
            Children.Add(Half(false, s, out bottomText, out _));    // fixa de baixo: mostra o antigo até a aba cair

            // Abas: encolhem/esticam na vertical a partir da linha do meio (simula a dobra)
            flapTop = Half(true, s, out flapTopText, out flapTopShade);
            flapBottom = Half(false, s, out flapBottomText, out flapBottomShade);
            flapTopScale = new ScaleTransform(1, 1, 0, h / 2);
            flapBottomScale = new ScaleTransform(1, 0, 0, h / 2);
            flapTop.RenderTransform = flapTopScale;
            flapBottom.RenderTransform = flapBottomScale;
            flapTop.Visibility = flapBottom.Visibility = Visibility.Hidden;
            Children.Add(flapTop);
            Children.Add(flapBottom);

            // Fenda do meio: faixa escura + fio de brilho logo abaixo (espessuras em pixels inteiros)
            double gapH = Math.Max(2, Math.Round(1.6 * s));
            double shineH = Math.Max(1, Math.Round(0.6 * s));
            var split = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, h / 2 - Math.Round(gapH / 2), 0, 0),
                IsHitTestVisible = false,
            };
            var gap = new Border { Height = gapH };
            gap.SetResourceReference(Border.BackgroundProperty, "Split");
            var shine = new Border { Height = shineH };
            shine.SetResourceReference(Border.BackgroundProperty, "SplitLight");
            split.Children.Add(gap);
            split.Children.Add(shine);
            Children.Add(split);

            // Contorno em relevo (luz no topo, sombra na base)
            var edge = new Border
            {
                CornerRadius = round,
                BorderThickness = new Thickness(Math.Max(1, Math.Round(0.6 * s))),
                IsHitTestVisible = false,
            };
            edge.SetResourceReference(Border.BorderBrushProperty, "CardEdge");
            Children.Add(edge);
        }

        // Metade da plaquinha: um cartão inteiro recortado (Clip) só na parte de cima ou de baixo.
        // "shade" é uma película preta usada para escurecer a aba enquanto ela dobra.
        Border Half(bool top, double s, out TextBlock text, out Border shade)
        {
            text = new TextBlock
            {
                FontFamily = Theme.Digits,
                FontSize = 50 * s,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            text.SetResourceReference(TextBlock.ForegroundProperty, "Digit");
            shade = new Border { Background = Brushes.Black, CornerRadius = round, Opacity = 0 };

            var inner = new Grid();
            inner.Children.Add(text);
            inner.Children.Add(shade);

            var card = new Border
            {
                Width = w,
                Height = h,
                CornerRadius = round,
                Child = inner,
                Clip = new RectangleGeometry(top ? new Rect(0, 0, w, h / 2) : new Rect(0, h / 2, w, h / 2)),
            };
            card.SetResourceReference(Border.BackgroundProperty, "CardBack");
            return card;
        }

        // Mostra o número; com animate = true a aba dobra
        public void Show(string next, bool animate)
        {
            if (next == value) return;
            string old = value;
            value = next;

            if (!animate || old == "")
            {
                topText.Text = bottomText.Text = flapTopText.Text = flapBottomText.Text = next;
                return;
            }

            topText.Text = old;           // até a metade do giro, o número mostrado continua o antigo
            bottomText.Text = old;        // embaixo continua o antigo até a aba de baixo cobrir
            flapTopText.Text = old;
            flapBottomText.Text = next;
            flapTop.Visibility = flapBottom.Visibility = Visibility.Visible;

            // 1ª metade: a aba de cima (número antigo) dobra para baixo e escurece
            var fold = new DoubleAnimation(1, 0, halfFlip)
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn },
            };
            fold.Completed += (_, _) => topText.Text = value;   // aba na metade do giro: só agora o número novo aparece
            flapTopScale.BeginAnimation(ScaleTransform.ScaleYProperty, fold);
            flapTopShade.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 0.55, halfFlip));

            // 2ª metade: a aba de baixo (número novo) abre a partir do meio e clareia
            var unfold = new DoubleAnimation(0, 1, halfFlip)
            {
                BeginTime = halfFlip,
                EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.2 },  // leve "quique"
            };
            unfold.Completed += (_, _) =>
            {
                bottomText.Text = value;
                flapTop.Visibility = flapBottom.Visibility = Visibility.Hidden;
            };
            flapBottomScale.BeginAnimation(ScaleTransform.ScaleYProperty, unfold);
            flapBottomShade.BeginAnimation(OpacityProperty, new DoubleAnimation(0.55, 0, halfFlip) { BeginTime = halfFlip });
        }
    }

    // ---------------------------------------------------------------
    // Uma plaquinha com o rótulo embaixo (ex.: "HORAS")
    // ---------------------------------------------------------------
    class FlipGroup : StackPanel
    {
        readonly Border holder = new Border();
        readonly double s;
        FlipCard? card;

        public FlipGroup(string label, double scale)
        {
            s = scale;
            Margin = new Thickness(Math.Round(4 * s), 0, Math.Round(4 * s), 0);
            Children.Add(holder);
            var text = Theme.Crisp(new TextBlock
            {
                Text = label,
                FontFamily = Theme.Ui,
                FontSize = 10 * s,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, Math.Round(7 * s), 0, 0),
            });
            text.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
            Children.Add(text);
        }

        public void Show(string digits, bool animate)
        {
            // Dias podem passar de 99: se mudar a quantidade de dígitos, troca por uma plaquinha mais larga
            if (card == null || card.Digits != digits.Length)
            {
                card = new FlipCard(digits.Length, s);
                holder.Child = card;
                animate = false;
            }
            card.Show(digits, animate);
        }
    }

    // ---------------------------------------------------------------
    // O contador em si (DIAS / HORAS / MINUTOS [/ SEGUNDOS] + mensagem final) do evento ativo.
    // Usado no mini pop-up (escala 1) e na tela cheia (escala grande).
    // ---------------------------------------------------------------
    class CounterView : Grid
    {
        public bool ShowsSeconds { get; }
        readonly AppConfig cfg;
        readonly double s;
        readonly FlipGroup[] groups;
        readonly StackPanel cards;
        readonly TextBlock arrivedText;
        readonly DispatcherTimer timer;
        long lastTotal = -1;                              // último total exibido (em minutos ou segundos)
        bool arrivedShown;
        bool forceAnimate;                                // próxima atualização vira as plaquinhas mesmo após Refresh

        public CounterView(AppConfig config, double scale)
        {
            cfg = config;
            s = scale;
            ShowsSeconds = cfg.ShowSeconds;

            cards = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            string[] labels = ShowsSeconds
                ? new[] { "DIAS", "HORAS", "MINUTOS", "SEGUNDOS" }
                : new[] { "DIAS", "HORAS", "MINUTOS" };
            groups = new FlipGroup[labels.Length];
            for (int i = 0; i < labels.Length; i++)
            {
                groups[i] = new FlipGroup(labels[i], s);
                cards.Children.Add(groups[i]);
            }

            // Mensagem final (escondida até chegar a zero)
            arrivedText = new TextBlock
            {
                Text = "CHEGOU O DIA!",
                FontFamily = Theme.Digits,
                FontSize = 34 * s,
                Effect = new DropShadowEffect { BlurRadius = 6 * s, ShadowDepth = 0, Opacity = 0.9 },
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = Visibility.Collapsed,
            };
            arrivedText.SetResourceReference(TextBlock.ForegroundProperty, "Accent");

            // Plaquinhas e mensagem no mesmo lugar; as plaquinhas ficam "Hidden" (não Collapsed)
            // para o tamanho não mudar quando a mensagem aparecer
            Children.Add(cards);
            Children.Add(arrivedText);
            UpdateGlowColor();

            // Relógio: agenda a próxima verificação para o instante exato em que o número muda
            timer = new DispatcherTimer();
            timer.Tick += (_, _) => Tick();
            Loaded += (_, _) => { timer.Start(); Tick(); };
            Unloaded += (_, _) => timer.Stop();
            Tick();
        }

        // Redesenha tudo (ex.: depois de trocar de evento); animate = as plaquinhas que mudarem viram
        public void Refresh(bool animate = false)
        {
            lastTotal = -1;
            forceAnimate = animate;
            Tick();
        }

        // A cor do brilho final não se atualiza sozinha pelo tema (efeitos não aceitam recurso dinâmico)
        public void UpdateGlowColor() => ((DropShadowEffect)arrivedText.Effect).Color = Theme.AccentColor;

        // Sempre "destino menos agora" (nunca um contador que reinicia)
        void Tick()
        {
            TimeSpan left = cfg.Active.Target - DateTime.Now;
            ScheduleNext(left);
            if (left <= TimeSpan.Zero)
            {
                ShowArrived(true);
                return;
            }
            ShowArrived(false);

            // Arredonda para cima: o último segundo/minuto continua aparecendo até zerar de verdade
            long total = ShowsSeconds ? (long)Math.Ceiling(left.TotalSeconds) : (long)Math.Ceiling(left.TotalMinutes);
            if (total == lastTotal) return;
            bool timePassed = lastTotal != -1;     // mudou porque o tempo passou (e não por um Refresh)
            bool animate = timePassed || forceAnimate;
            forceAnimate = false;
            lastTotal = total;

            long[] parts = ShowsSeconds
                ? new[] { total / 86400, total % 86400 / 3600, total % 3600 / 60, total % 60 }   // dias, horas, minutos, segundos
                : new[] { total / 1440, total % 1440 / 60, total % 60 };                        // dias, horas, minutos
            for (int i = 0; i < parts.Length; i++)
                groups[i].Show(parts[i].ToString("00"), animate);   // só as plaquinhas que mudaram viram

            // Tique-taque no compasso das viradas (só o contador que está na tela)
            if (timePassed && ShowsSeconds && IsVisible) ClockSound.OnSecond();
        }

        // Próxima verificação logo depois da próxima troca de número (no máximo 1 s)
        void ScheduleNext(TimeSpan left)
        {
            double unit = ShowsSeconds ? 1000 : 60000;
            double ms = left.TotalMilliseconds % unit;   // quanto falta para o número mudar
            if (ms <= 0) ms += unit;
            timer.Interval = TimeSpan.FromMilliseconds(Math.Clamp(ms + 15, 20, 1000));
        }

        // Liga/desliga a tela "CHEGOU O DIA!" com brilho pulsando
        void ShowArrived(bool arrived)
        {
            if (arrived == arrivedShown) return;
            arrivedShown = arrived;
            ClockSound.Apply(cfg);   // o tique-taque para quando a contagem termina
            cards.Visibility = arrived ? Visibility.Hidden : Visibility.Visible;
            arrivedText.Visibility = arrived ? Visibility.Visible : Visibility.Collapsed;

            var glow = (DropShadowEffect)arrivedText.Effect;
            if (arrived)
            {
                var pulse = TimeSpan.FromSeconds(0.9);
                glow.BeginAnimation(DropShadowEffect.BlurRadiusProperty,
                    new DoubleAnimation(4 * s, 28 * s, pulse) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
                arrivedText.BeginAnimation(OpacityProperty,
                    new DoubleAnimation(0.75, 1, pulse) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
            }
            else
            {
                // null = remove a animação e volta ao valor original
                glow.BeginAnimation(DropShadowEffect.BlurRadiusProperty, null);
                arrivedText.BeginAnimation(OpacityProperty, null);
                lastTotal = -1;
            }
        }
    }

    // ---------------------------------------------------------------
    // Carrossel pelas setas do teclado: o 1º toque mostra uma dica por cima do contador
    // (">" = ir para o próximo timer, "+" = criar um novo) e o 2º toque seguido confirma.
    // Sem o 2º toque, a dica some sozinha.
    // ---------------------------------------------------------------
    class CarouselHint : Grid
    {
        readonly UIElement content;
        readonly StackPanel hint;
        readonly TextBlock glyph, caption;
        readonly TranslateTransform glyphMove = new TranslateTransform();
        readonly ScaleTransform glyphScale = new ScaleTransform();
        readonly RotateTransform glyphTurn = new RotateTransform();
        readonly DispatcherTimer expire = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.8) };   // prazo do 2º toque
        Key? armed;   // seta apertada uma vez, esperando a segunda
        double s = 1;

        public bool IsShowing => armed != null;
        public event Action? ShowingChanged;   // a dica apareceu ou sumiu

        public CarouselHint(UIElement content, double scale)
        {
            this.content = content;

            glyph = new TextBlock
            {
                FontFamily = Theme.Icons,
                HorizontalAlignment = HorizontalAlignment.Center,
                RenderTransformOrigin = new Point(0.5, 0.5),
            };
            glyph.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
            var transforms = new TransformGroup();
            transforms.Children.Add(glyphScale);
            transforms.Children.Add(glyphTurn);
            transforms.Children.Add(glyphMove);
            glyph.RenderTransform = transforms;

            caption = Theme.Text("", 11, "Title");
            caption.TextAlignment = TextAlignment.Center;
            caption.HorizontalAlignment = HorizontalAlignment.Center;

            // A dica fica no mesmo lugar do contador, sem mudar o tamanho dele
            hint = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false,
                Opacity = 0,
            };
            hint.Children.Add(glyph);
            hint.Children.Add(caption);

            Children.Add(content);
            Children.Add(hint);
            SetScale(scale);

            expire.Tick += (_, _) =>
            {
                expire.Stop();
                armed = null;
                Hide();
            };
        }

        // Texto da dica para cada seta
        public static string Message(Key key, AppConfig cfg) =>
            key == Key.Left ? "Aperte ← de novo para criar um novo timer"
            : cfg.Events.Count > 1 ? "Aperte → de novo para ir ao próximo timer"
            : "Este é o seu único timer. Aperte ← duas vezes para criar outro";

        // Tamanho da dica acompanha o do contador (a tela cheia usa escala grande)
        public void SetScale(double scale)
        {
            s = scale;
            glyph.FontSize = 34 * s;
            caption.FontSize = 11 * s;
            caption.MaxWidth = 220 * s;
            caption.Margin = new Thickness(0, 6 * s, 0, 0);
        }

        // Um toque numa seta; retorna true no 2º toque seguido da mesma seta (hora de agir)
        public bool Press(Key key, string message)
        {
            expire.Stop();
            if (armed == key)
            {
                armed = null;
                Hide();
                return true;
            }
            armed = key;
            Show(key == Key.Right, message);
            expire.Start();
            return false;
        }

        // Desfaz o 1º toque sem agir (ex.: apertou outra tecla no meio)
        public void Cancel()
        {
            if (armed == null) return;
            expire.Stop();
            armed = null;
            Hide();
        }

        void Show(bool next, string message)
        {
            glyph.Text = next ? Theme.GlyphNext : Theme.GlyphAdd;
            caption.Text = message;
            Fade(hint, 1, 160);
            Fade(content, 0.1, 160);   // o contador fica apagadinho por trás
            StopGlyph();
            ShowingChanged?.Invoke();

            if (next)
            {
                // ">" deslizando para a direita sem parar, apontando para o próximo timer
                var duration = TimeSpan.FromMilliseconds(900);
                var slide = new DoubleAnimationUsingKeyFrames { Duration = duration, RepeatBehavior = RepeatBehavior.Forever };
                slide.KeyFrames.Add(new LinearDoubleKeyFrame(-14 * s, KeyTime.FromPercent(0)));
                slide.KeyFrames.Add(new EasingDoubleKeyFrame(14 * s, KeyTime.FromPercent(0.8))
                {
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
                });
                slide.KeyFrames.Add(new DiscreteDoubleKeyFrame(14 * s, KeyTime.FromPercent(1)));
                var blink = new DoubleAnimationUsingKeyFrames { Duration = duration, RepeatBehavior = RepeatBehavior.Forever };
                blink.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(0)));
                blink.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromPercent(0.3)));
                blink.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromPercent(0.6)));
                blink.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(1)));
                glyphMove.BeginAnimation(TranslateTransform.XProperty, slide);
                glyph.BeginAnimation(OpacityProperty, blink);
            }
            else
            {
                // "+" surgindo com um giro e um quique
                var duration = TimeSpan.FromMilliseconds(450);
                var grow = new DoubleAnimation(0.3, 1, duration) { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.6 } };
                glyphScale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
                glyphScale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
                glyphTurn.BeginAnimation(RotateTransform.AngleProperty,
                    new DoubleAnimation(-90, 0, duration) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
            }
        }

        void Hide()
        {
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(220));
            fade.Completed += (_, _) => { if (armed == null) StopGlyph(); };   // para o ">" que repete sem fim
            hint.BeginAnimation(OpacityProperty, fade);
            Fade(content, 1, 220);
            ShowingChanged?.Invoke();
        }

        // null = remove a animação e volta ao valor original
        void StopGlyph()
        {
            glyphMove.BeginAnimation(TranslateTransform.XProperty, null);
            glyph.BeginAnimation(OpacityProperty, null);
            glyphScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            glyphScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            glyphTurn.BeginAnimation(RotateTransform.AngleProperty, null);
        }

        static void Fade(UIElement element, double to, int ms) =>
            element.BeginAnimation(OpacityProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms)));
    }

    // ---------------------------------------------------------------
    // Janela principal: o mini pop-up
    // ---------------------------------------------------------------
    class CountdownWindow : Window
    {
        const double TransparentOpacity = 0.45;   // opacidade no modo transparente (0 = invisível, 1 = sólido)

        readonly AppConfig cfg;
        readonly TextBlock title, brush;
        readonly Border counterHost = new Border();
        readonly UIElement mainView;   // a página do contador (para voltar das configurações)
        readonly DispatcherTimer savePosition = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        readonly CarouselHint carousel;   // dica das setas por cima do contador
        CounterView counter;
        FullscreenWindow? fullscreen;
        bool settingsOpen, newTimerOpen;

        public CountdownWindow(AppConfig config)
        {
            cfg = config;
            Theme.SetupFloating(this);
            Title = "Countdown";
            carousel = new CarouselHint(counterHost, 1);
            carousel.ShowingChanged += UpdateOpacity;   // no modo transparente, fica sólido para dar para ler a dica

            WindowStartupLocation = WindowStartupLocation.Manual;
            Loaded += (_, _) =>
            {
                PlaceOnScreen();
                ClockSound.Apply(cfg);   // começa o tique-taque (se estiver ligado)
            };
            // O tamanho muda ao abrir as configurações ou ligar os segundos
            SizeChanged += (_, e) =>
            {
                if (!IsLoaded || e.PreviousSize.Width <= 0) return;
                if (e.WidthChanged) Left -= e.NewSize.Width - e.PreviousSize.Width;   // mantém a borda direita no lugar
                // Não deixa a janela sair da tela (área de todos os monitores juntos)
                double bottom = SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight;
                Left = Math.Max(SystemParameters.VirtualScreenLeft, Left);
                if (Top + ActualHeight > bottom) Top = Math.Max(SystemParameters.VirtualScreenTop, bottom - ActualHeight);
            };
            // Lembra onde você deixou o pop-up (salva um instante depois de parar de mexer)
            LocationChanged += (_, _) =>
            {
                if (!IsLoaded) return;
                savePosition.Stop();
                savePosition.Start();
            };
            savePosition.Tick += (_, _) =>
            {
                savePosition.Stop();
                cfg.PopupRight = Left + ActualWidth;
                cfg.PopupTop = Top;
                Storage.Save(cfg);
            };
            // Esc nas configurações ou no novo timer = voltar
            KeyDown += (_, e) =>
            {
                if (e.Key != Key.Escape) return;
                if (settingsOpen) CloseSettings();
                else if (newTimerOpen) CloseNewTimer();
            };
            // Setas: carrossel (→ → próximo timer, ← ← criar um novo)
            PreviewKeyDown += (_, e) =>
            {
                if (settingsOpen || newTimerOpen || e.IsRepeat || (e.Key != Key.Left && e.Key != Key.Right)) return;
                e.Handled = true;
                if (!carousel.Press(e.Key, CarouselHint.Message(e.Key, cfg))) return;
                if (e.Key == Key.Left) OpenNewTimer();
                else if (cfg.Events.Count > 1) NextTimer();
            };

            // --- Cabeçalho: pincel (tema) à esquerda, nome no centro, tela cheia/configurações/fechar à direita ---
            title = Theme.Crisp(new TextBlock
            {
                FontFamily = Theme.Ui,
                FontSize = 14,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,   // "..." se o nome for longo
                Margin = new Thickness(60, 0, 60, 0),            // mesmo espaço dos dois lados: nome sempre no centro
            });
            title.SetResourceReference(TextBlock.ForegroundProperty, "Title");

            brush = Theme.Icon(Theme.GlyphBrush, "", ToggleTheme);
            brush.HorizontalAlignment = HorizontalAlignment.Left;

            var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            right.Children.Add(Theme.Icon(Theme.GlyphFullScreen, "Tela cheia", EnterFullscreen));
            right.Children.Add(Theme.Icon(Theme.GlyphGear, "Configurações", OpenSettings));
            right.Children.Add(Theme.Icon(Theme.GlyphClose, "Fechar", Close));

            var header = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            header.Children.Add(title);
            header.Children.Add(brush);
            header.Children.Add(right);

            counter = new CounterView(cfg, 1);
            counterHost.Child = counter;

            // ClipToBounds: ao trocar de timer, o contador entra deslizando sem passar da borda
            var root = new StackPanel { Margin = new Thickness(14, 8, 14, 12), MinWidth = 250, ClipToBounds = true };
            root.Children.Add(header);
            root.Children.Add(carousel);
            mainView = Theme.Panel(root);
            Content = mainView;

            // Modo transparente: fica sólido enquanto o mouse está em cima, para dar para ler e clicar
            MouseEnter += (_, _) => UpdateOpacity();
            MouseLeave += (_, _) => UpdateOpacity();

            UpdateThemeIcon();
            ApplySettings();
        }

        // Volta para onde você deixou da última vez; se não couber na tela, canto superior direito
        void PlaceOnScreen()
        {
            var area = SystemParameters.WorkArea;
            double left = area.Right - ActualWidth - 6, top = area.Top + 6;
            if (cfg.PopupRight is double r && cfg.PopupTop is double t)
            {
                double screenLeft = SystemParameters.VirtualScreenLeft, screenTop = SystemParameters.VirtualScreenTop;
                double screenRight = screenLeft + SystemParameters.VirtualScreenWidth;
                double screenBottom = screenTop + SystemParameters.VirtualScreenHeight;
                if (r - ActualWidth >= screenLeft && r <= screenRight && t >= screenTop && t + ActualHeight <= screenBottom)
                {
                    left = r - ActualWidth;
                    top = t;
                }
            }
            Left = left;
            Top = top;
        }

        // Aplica tudo o que pode ter mudado nas configurações (as plaquinhas que mudarem viram)
        void ApplySettings()
        {
            title.Text = cfg.Active.Name.ToUpperInvariant();
            if (counter.ShowsSeconds != cfg.ShowSeconds)
            {
                counter = new CounterView(cfg, 1);   // com ou sem a plaquinha de segundos
                counterHost.Child = counter;
            }
            else
            {
                counter.Refresh(animate: true);
            }
            counter.UpdateGlowColor();
            UpdateOpacity();
        }

        // Pincel: alterna claro/escuro e salva a escolha
        void ToggleTheme()
        {
            cfg.DarkMode = !cfg.DarkMode;
            Theme.Apply(cfg.DarkMode);
            Storage.Save(cfg);
            UpdateThemeIcon();
            counter.UpdateGlowColor();
            Theme.Wiggle(brush);
        }

        void UpdateThemeIcon() => brush.ToolTip = cfg.DarkMode ? "Mudar para o tema claro" : "Mudar para o tema escuro";

        // Transição suave entre transparente e sólido
        void UpdateOpacity()
        {
            bool solid = IsMouseOver || settingsOpen || newTimerOpen || carousel.IsShowing;
            double target = cfg.Transparent && !solid ? TransparentOpacity : 1;
            BeginAnimation(OpacityProperty, new DoubleAnimation(target, TimeSpan.FromMilliseconds(250)));
        }

        // Troca a página mostrada na janela, deslizando de lado
        void ShowView(UIElement view, double fromX)
        {
            Content = view;
            Theme.SlideIn(view, fromX, 0);
        }

        // As configurações abrem no lugar do contador, dentro da mesma janela
        void OpenSettings()
        {
            settingsOpen = true;
            ShowView(Theme.Panel(new SettingsPage(cfg, UpdateOpacity, CloseSettings)), 24);
            UpdateOpacity();
        }

        void CloseSettings()
        {
            settingsOpen = false;
            ShowView(mainView, -24);
            ApplySettings();   // depois de voltar à tela: se trocou de evento, as plaquinhas viram para os novos números
        }

        // Carrossel: o próximo timer entra pela direita (depois do último, volta ao primeiro)
        void NextTimer()
        {
            cfg.ActiveEvent = (cfg.ActiveEvent + 1) % cfg.Events.Count;
            Storage.Save(cfg);
            Theme.SlideIn(title, 40, 0);
            Theme.SlideIn(counterHost, 40, 0);
            ApplySettings();
        }

        // Carrossel: à esquerda dos timers fica a página de criar um novo
        void OpenNewTimer()
        {
            newTimerOpen = true;
            ShowView(Theme.Panel(new NewTimerPage(ev =>
            {
                cfg.Events.Add(ev);
                cfg.ActiveEvent = cfg.Events.Count - 1;   // o novo timer já aparece no contador
                Storage.Save(cfg);
                CloseNewTimer();
            }, CloseNewTimer)), -24);
            UpdateOpacity();
        }

        void CloseNewTimer()
        {
            newTimerOpen = false;
            ShowView(mainView, 24);
            ApplySettings();
            Focus();   // o campo de texto saiu da tela: o teclado volta para a janela (setas continuam valendo)
        }

        // Esconde o mini pop-up e abre a tela cheia no mesmo monitor; ao sair, ele volta
        void EnterFullscreen()
        {
            fullscreen = new FullscreenWindow(cfg, new Point(Left + ActualWidth / 2, Top + ActualHeight / 2));
            fullscreen.Closed += (_, _) =>
            {
                fullscreen = null;
                UpdateThemeIcon();
                Show();
                Activate();
                ApplySettings();   // algo pode ter mudado nas configurações abertas pela tela cheia
            };
            Hide();
            fullscreen.Show();
        }

        // Chamado quando você tenta abrir o app de novo: traz a janela que já está aberta para a frente
        public void BringToFront()
        {
            if (fullscreen != null)
            {
                fullscreen.Activate();
                return;
            }
            Show();
            Activate();
            if (Content is FrameworkElement view) Theme.Pop(view);
        }
    }

    // ---------------------------------------------------------------
    // Tela cheia: contador grande + frases motivadoras flutuando
    // ---------------------------------------------------------------
    class FullscreenWindow : Window
    {
        // Uma frase flutuando: o texto na tela, sua posição e sua velocidade
        class FloatingPhrase
        {
            public string Text = "";
            public TextBlock Element = null!;
            public TranslateTransform Move = null!;
            public double X, Y, VX, VY;
        }

        readonly AppConfig cfg;
        readonly Grid stage = new Grid();   // onde ficam título e contador
        readonly TranslateTransform stageShift = new TranslateTransform();   // sobe o contador quando o painel de frases abre
        readonly Canvas phraseLayer = new Canvas { ClipToBounds = true, IsHitTestVisible = false };
        readonly TextBlock phrasesIcon, brush;
        readonly TextBox phraseBox;
        readonly StackPanel phraseList = new StackPanel();
        readonly Border editor;
        readonly List<FloatingPhrase> floating = new List<FloatingPhrase>();
        readonly Random random = new Random();
        readonly Grid mainLayer = new Grid();        // tudo da tela cheia (frases, contador, ícones, painel)
        readonly Border settingsLayer = new Border   // configurações, no lugar do conteúdo
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        readonly CarouselHint carousel;   // dica das setas por cima do contador
        readonly CarouselHint pageCarousel;   // dica das setas por cima da página de novo timer (tela toda)
        TextBlock? stageTitle;
        CounterView? stageCounter;
        double scale;
        TimeSpan lastFrame;

        public FullscreenWindow(AppConfig config, Point center)
        {
            cfg = config;
            Title = "Countdown";
            Icon = Theme.AppIcon;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;   // junto com WindowStyle.None, faz o maximizado cobrir também a barra de tarefas
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;
            SetResourceReference(BackgroundProperty, "PanelBack");

            // Abre no mesmo monitor do mini pop-up: posiciona a janela lá e então maximiza
            WindowStartupLocation = WindowStartupLocation.Manual;
            Width = 400;
            Height = 300;
            Left = center.X - Width / 2;
            Top = center.Y - Height / 2;
            WindowState = WindowState.Maximized;

            // Pincel (tema) no canto superior esquerdo
            brush = Theme.Icon(Theme.GlyphBrush, "Trocar tema claro/escuro", ToggleTheme, 18);
            brush.HorizontalAlignment = HorizontalAlignment.Left;
            brush.VerticalAlignment = VerticalAlignment.Top;
            brush.Margin = new Thickness(16, 16, 0, 0);

            // Ícones no canto superior direito
            phrasesIcon = Theme.Icon(Theme.GlyphEdit, "Frases motivadoras", ToggleEditor, 18);
            var toolbar = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 16, 16, 0),
            };
            toolbar.Children.Add(phrasesIcon);
            toolbar.Children.Add(Theme.Icon(Theme.GlyphGear, "Configurações", OpenSettings, 18));
            toolbar.Children.Add(Theme.Icon(Theme.GlyphBackToWindow, "Voltar ao mini pop-up (Esc)", Close, 18));

            phraseBox = Theme.Input("", 15);
            editor = BuildEditor();
            editor.SizeChanged += (_, _) => UpdateStageShift();   // painel cresce ao adicionar frases
            foreach (string p in cfg.Phrases) AddRow(p);
            stage.RenderTransform = stageShift;

            // Camadas, de trás para frente: frases, contador, ícones, painel de frases
            carousel = new CarouselHint(stage, 1);
            mainLayer.Children.Add(phraseLayer);
            mainLayer.Children.Add(carousel);
            mainLayer.Children.Add(brush);
            mainLayer.Children.Add(toolbar);
            mainLayer.Children.Add(editor);
            pageCarousel = new CarouselHint(settingsLayer, 1);
            var root = new Grid();
            root.Children.Add(mainLayer);
            root.Children.Add(pageCarousel);
            Content = root;

            // O contador é (re)construído no tamanho exato da tela, sem ampliar imagem
            SizeChanged += (_, _) => BuildStage();
            // Depois do primeiro desenho a tela já tem tamanho: solta as frases e liga a animação
            ContentRendered += (_, _) =>
            {
                foreach (string p in cfg.Phrases) Spawn(p);
                CompositionTarget.Rendering += OnFrame;   // chamado a cada quadro da tela (movimento suave)
            };
            Closed += (_, _) => CompositionTarget.Rendering -= OnFrame;

            // Esc: volta das configurações/novo timer; senão fecha o painel de frases; senão sai da tela cheia
            KeyDown += (_, e) =>
            {
                if (e.Key != Key.Escape) return;
                e.Handled = true;
                if (settingsLayer.Child != null) ClosePage();
                else if (editor.Visibility == Visibility.Visible) ToggleEditor();
                else Close();
            };
            // Setas: carrossel (→ → próximo timer, ← ← criar um novo); no campo de frase, as setas movem o cursor
            PreviewKeyDown += (_, e) =>
            {
                if (settingsLayer.Child != null || Keyboard.FocusedElement is TextBox || e.IsRepeat) return;
                if (e.Key != Key.Left && e.Key != Key.Right) return;
                e.Handled = true;
                if (!carousel.Press(e.Key, CarouselHint.Message(e.Key, cfg))) return;
                if (e.Key == Key.Left) OpenNewTimer();
                else if (cfg.Events.Count > 1) NextTimer();
            };
        }

        // Calcula a escala para o contador ocupar boa parte da tela e o monta nesse tamanho
        void BuildStage()
        {
            if (ActualWidth < 100 || ActualHeight < 100) return;
            double baseWidth = (cfg.ShowSeconds ? 4 : 3) * 74 + 8;   // largura do contador em escala 1
            const double baseHeight = 125;                            // título + plaquinhas + rótulos em escala 1
            double s = Math.Min(ActualWidth * 0.62 / baseWidth, ActualHeight * 0.5 / baseHeight);
            if (Math.Abs(s - scale) < 0.01) return;   // tamanho não mudou de verdade
            scale = s;

            stageTitle = new TextBlock
            {
                Text = cfg.Active.Name.ToUpperInvariant(),
                FontFamily = Theme.Ui,
                FontSize = 13 * s,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = ActualWidth * 0.9,
                Margin = new Thickness(0, 0, 0, Math.Round(12 * s)),
            };
            stageTitle.SetResourceReference(TextBlock.ForegroundProperty, "Title");
            stageCounter = new CounterView(cfg, s);

            var column = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            column.Children.Add(stageTitle);
            column.Children.Add(stageCounter);
            stage.Children.Clear();
            stage.Children.Add(column);
            carousel.SetScale(s);
            pageCarousel.SetScale(s);
            UpdateStageShift();
        }

        // Com o painel de frases aberto, sobe o contador só o necessário para não ficar coberto
        void UpdateStageShift()
        {
            double target = 0;
            if (editor.Visibility == Visibility.Visible && stage.Children.Count > 0)
            {
                UpdateLayout();
                var column = (FrameworkElement)stage.Children[0];
                Rect bounds = column.TransformToAncestor(stage).TransformBounds(new Rect(column.RenderSize));
                double editorTop = ActualHeight - editor.Margin.Bottom - editor.ActualHeight;
                target = Math.Min(0, editorTop - 24 - bounds.Bottom);   // distância que falta para não encostar
                target = Math.Max(target, 16 - bounds.Top);             // sem passar do topo da tela
            }
            stageShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(target, TimeSpan.FromMilliseconds(300))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
            });
        }

        void ToggleTheme()
        {
            cfg.DarkMode = !cfg.DarkMode;
            Theme.Apply(cfg.DarkMode);
            Storage.Save(cfg);
            stageCounter?.UpdateGlowColor();
            Theme.Wiggle(brush);
        }

        // Configurações no lugar do conteúdo da tela cheia ("Hidden" mantém tudo montado por baixo)
        void OpenSettings()
        {
            mainLayer.Visibility = Visibility.Hidden;
            var page = Theme.Panel(new SettingsPage(cfg, () => { }, ClosePage));
            settingsLayer.Child = page;
            Theme.SlideIn(page, 0, 16);
        }

        // Carrossel: a página de criar um novo timer entra pela esquerda, no mesmo lugar das configurações
        void OpenNewTimer()
        {
            mainLayer.Visibility = Visibility.Hidden;
            var page = Theme.Panel(new NewTimerPage(ev =>
            {
                cfg.Events.Add(ev);
                cfg.ActiveEvent = cfg.Events.Count - 1;   // o novo timer já aparece no contador
                Storage.Save(cfg);
                ClosePage();
            }, ClosePage, pageCarousel));
            settingsLayer.Child = page;
            Theme.SlideIn(page, -24, 0);
        }

        // Carrossel: o próximo timer entra pela direita (depois do último, volta ao primeiro)
        void NextTimer()
        {
            cfg.ActiveEvent = (cfg.ActiveEvent + 1) % cfg.Events.Count;
            Storage.Save(cfg);
            if (stage.Children.Count > 0) Theme.SlideIn(stage.Children[0], 12 * scale, 0);
            if (stageTitle != null) stageTitle.Text = cfg.Active.Name.ToUpperInvariant();
            stageCounter?.Refresh(animate: true);
        }

        // Fecha a página aberta no lugar do conteúdo (configurações ou novo timer)
        void ClosePage()
        {
            settingsLayer.Child = null;
            pageCarousel.Cancel();   // saiu com Esc ou X no meio do 1º toque: some com a dica
            mainLayer.Visibility = Visibility.Visible;
            Theme.SlideIn(mainLayer, 0, 0);
            Focus();   // o campo de texto saiu da tela: o teclado volta para a janela
            if (stageCounter == null || stageCounter.ShowsSeconds != cfg.ShowSeconds)
            {
                scale = 0;   // ligou/desligou os segundos: remonta o contador
                BuildStage();
            }
            else
            {
                if (stageTitle != null) stageTitle.Text = cfg.Active.Name.ToUpperInvariant();
                stageCounter.Refresh(animate: true);   // trocou de evento: as plaquinhas viram para os novos números
            }
        }

        // --- Painel de frases: campo para escrever + lista com botão de apagar ---
        Border BuildEditor()
        {
            var heading = Theme.Text("FRASES MOTIVADORAS", 13, "Accent", FontWeights.SemiBold);
            heading.VerticalAlignment = VerticalAlignment.Center;
            var close = Theme.Icon(Theme.GlyphClose, "Fechar (Esc)", ToggleEditor, 12);
            var header = new DockPanel();
            DockPanel.SetDock(close, Dock.Right);
            header.Children.Add(close);
            header.Children.Add(heading);

            // Texto de dica dentro do campo vazio (o TextBox do WPF não tem "placeholder" pronto)
            var placeholder = Theme.Text("Escreva uma frase e aperte Enter", 15, "Label");
            placeholder.IsHitTestVisible = false;
            placeholder.VerticalAlignment = VerticalAlignment.Center;
            placeholder.Margin = new Thickness(9, 0, 0, 0);
            phraseBox.TextChanged += (_, _) =>
                placeholder.Visibility = phraseBox.Text.Length == 0 ? Visibility.Visible : Visibility.Hidden;
            phraseBox.KeyDown += (_, e) =>
            {
                if (e.Key != Key.Enter) return;
                e.Handled = true;
                AddPhrase();
            };
            var boxArea = new Grid();
            boxArea.Children.Add(phraseBox);
            boxArea.Children.Add(placeholder);

            var add = Theme.Icon(Theme.GlyphAdd, "Adicionar", AddPhrase, 16);
            add.Margin = new Thickness(8, 0, 0, 0);
            var inputRow = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
            DockPanel.SetDock(add, Dock.Right);
            inputRow.Children.Add(add);
            inputRow.Children.Add(boxArea);

            var list = new ScrollViewer
            {
                MaxHeight = 180,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = phraseList,
                Margin = new Thickness(0, 8, 0, 0),
            };

            var panel = new StackPanel { Width = 520, Margin = new Thickness(20, 16, 20, 18) };
            panel.Children.Add(header);
            panel.Children.Add(inputRow);
            panel.Children.Add(list);

            var border = new Border
            {
                CornerRadius = new CornerRadius(12),
                BorderThickness = new Thickness(1),
                Child = panel,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 0, 48),
                Visibility = Visibility.Collapsed,
            };
            border.SetResourceReference(Border.BackgroundProperty, "EditorBack");
            border.SetResourceReference(Border.BorderBrushProperty, "PanelBorder");
            return border;
        }

        void ToggleEditor()
        {
            bool open = editor.Visibility != Visibility.Visible;
            editor.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            Theme.SetActive(phrasesIcon, open);
            UpdateStageShift();
            if (open)
            {
                Theme.SlideIn(editor, 0, 20);
                phraseBox.Focus();
            }
        }

        void AddPhrase()
        {
            string text = phraseBox.Text.Trim();
            if (text.Length == 0) return;
            cfg.Phrases.Add(text);
            Storage.Save(cfg);
            phraseBox.Clear();
            var row = AddRow(text);
            Theme.SlideIn(row, 0, -6);
            Spawn(text);
        }

        // Uma linha da lista: a frase + lixeira (apaga em dois cliques)
        DockPanel AddRow(string text)
        {
            var row = new DockPanel { Margin = new Thickness(0, 6, 4, 0) };
            var delete = Theme.DeleteButton(() => RemovePhrase(text, row), 13);
            DockPanel.SetDock(delete, Dock.Right);
            var label = Theme.Text(text, 14, "InputFore");
            label.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(delete);
            row.Children.Add(label);
            phraseList.Children.Add(row);
            return row;
        }

        void RemovePhrase(string text, UIElement row)
        {
            cfg.Phrases.Remove(text);
            Storage.Save(cfg);
            phraseList.Children.Remove(row);

            var p = floating.Find(f => f.Text == text);
            if (p == null) return;
            floating.Remove(p);
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(500));   // some devagar
            fade.Completed += (_, _) => phraseLayer.Children.Remove(p.Element);
            p.Element.BeginAnimation(OpacityProperty, fade);
        }

        // Cria uma frase flutuante em posição, tamanho, cor e direção aleatórios
        void Spawn(string text)
        {
            double width = Math.Max(phraseLayer.ActualWidth, 1);
            double height = Math.Max(phraseLayer.ActualHeight, 1);
            double unit = Math.Max(height / 1080, 0.6);   // tamanhos proporcionais à altura da tela

            var t = new TextBlock
            {
                Text = text,
                FontFamily = Theme.Ui,
                FontWeight = FontWeights.Light,
                FontSize = (22 + random.NextDouble() * 18) * unit,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                MaxWidth = width * 0.45,
                Opacity = 0,
            };
            // Renderização de texto própria para texto em movimento (evita "tremido")
            TextOptions.SetTextHintingMode(t, TextHintingMode.Animated);
            t.SetResourceReference(TextBlock.ForegroundProperty, random.NextDouble() < 0.3 ? "Accent" : "Title");
            var move = new TranslateTransform();
            t.RenderTransform = move;
            phraseLayer.Children.Add(t);
            t.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

            double angle = random.NextDouble() * Math.PI * 2;
            double speed = (18 + random.NextDouble() * 22) * unit;   // pixels por segundo
            var p = new FloatingPhrase
            {
                Text = text,
                Element = t,
                Move = move,
                X = random.NextDouble() * Math.Max(0, width - t.DesiredSize.Width),
                Y = random.NextDouble() * Math.Max(0, height - t.DesiredSize.Height),
                VX = Math.Cos(angle) * speed,
                VY = Math.Sin(angle) * speed,
            };
            move.X = p.X;
            move.Y = p.Y;
            floating.Add(p);

            // Aparece aos poucos, ficando semitransparente (não compete com o contador)
            t.BeginAnimation(OpacityProperty, new DoubleAnimation(0.3 + random.NextDouble() * 0.3, TimeSpan.FromSeconds(1.5)));
        }

        // A cada quadro: move as frases e faz elas "quicarem" nas bordas da tela
        void OnFrame(object? sender, EventArgs e)
        {
            var now = ((RenderingEventArgs)e).RenderingTime;
            double dt = (now - lastFrame).TotalSeconds;   // segundos desde o último quadro
            lastFrame = now;
            if (dt <= 0 || dt > 0.25) return;            // ignora o primeiro quadro e pausas longas

            double width = phraseLayer.ActualWidth, height = phraseLayer.ActualHeight;
            foreach (var p in floating)
            {
                double w = p.Element.ActualWidth, h = p.Element.ActualHeight;
                p.X += p.VX * dt;
                p.Y += p.VY * dt;
                if (p.X < 0) { p.X = 0; p.VX = Math.Abs(p.VX); }
                else if (p.X + w > width) { p.X = Math.Max(0, width - w); p.VX = -Math.Abs(p.VX); }
                if (p.Y < 0) { p.Y = 0; p.VY = Math.Abs(p.VY); }
                else if (p.Y + h > height) { p.Y = Math.Max(0, height - h); p.VY = -Math.Abs(p.VY); }
                p.Move.X = p.X;
                p.Move.Y = p.Y;
            }
        }
    }

    // ---------------------------------------------------------------
    // Campos de nome e data de um evento, com validação e botão salvar (primeira execução e novo timer)
    // ---------------------------------------------------------------
    class EventForm : StackPanel
    {
        readonly TextBox nameBox;
        readonly TextBox dateBox;
        readonly TextBlock message;
        public event Action<string, DateTime>? Saved;

        public EventForm()
        {
            nameBox = Theme.Input("");
            dateBox = Theme.Input(DateInput.ToText(DateTime.Now.AddDays(1)));   // sugestão: amanhã, mesmo horário
            nameBox.KeyDown += EnterSaves;
            dateBox.KeyDown += EnterSaves;

            message = Theme.Text("", 11, "Error");
            message.Margin = new Thickness(0, 6, 0, 0);
            message.Visibility = Visibility.Collapsed;

            var save = Theme.Button("SALVAR", Save);
            save.Margin = new Thickness(0, 14, 0, 0);

            Children.Add(Theme.Caption("Nome do evento"));
            Children.Add(nameBox);
            Children.Add(Theme.Caption("Data e hora (dd/mm/aaaa hh:mm)"));
            Children.Add(dateBox);
            Children.Add(message);
            Children.Add(save);
        }

        void EnterSaves(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            Save();
        }

        public void FocusName()
        {
            nameBox.Focus();
            nameBox.SelectAll();
        }

        // Valida os campos; se estiver tudo certo, avisa quem estiver ouvindo o evento Saved
        void Save()
        {
            string name = nameBox.Text.Trim();
            if (name.Length == 0)
            {
                ShowError("Digite o nome do evento.");
                return;
            }
            if (!DateInput.TryParse(dateBox.Text, out DateTime target))
            {
                ShowError("Data inválida. Use o formato 25/12/2026 18:30.");
                return;
            }
            Saved?.Invoke(name, target);
        }

        void ShowError(string text)
        {
            message.Text = text;
            message.Visibility = Visibility.Visible;
            Theme.SlideIn(message, 6, 0);
        }
    }

    // ---------------------------------------------------------------
    // Primeira execução: pede só o nome e a data do primeiro evento
    // ---------------------------------------------------------------
    class FirstRunWindow : Window
    {
        public AppConfig? Result { get; private set; }   // null = cancelado

        public FirstRunWindow()
        {
            Theme.SetupFloating(this);
            Title = "Countdown - Novo evento";
            WindowStartupLocation = WindowStartupLocation.CenterScreen;

            var header = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            var close = Theme.Icon(Theme.GlyphClose, "Cancelar", Close);
            DockPanel.SetDock(close, Dock.Right);
            header.Children.Add(close);
            var heading = Theme.Text("CONFIGURAR EVENTO", 12, "Accent", FontWeights.SemiBold);
            heading.VerticalAlignment = VerticalAlignment.Center;
            header.Children.Add(heading);

            var form = new EventForm();
            form.Saved += (name, target) =>
            {
                Result = new AppConfig();
                Result.Events.Add(new EventItem { Name = name, Target = target });
                Close();
            };

            var panel = new StackPanel { Width = 240, Margin = new Thickness(16) };
            panel.Children.Add(header);
            panel.Children.Add(form);
            Content = Theme.Panel(panel);

            KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
            Loaded += (_, _) => form.FocusName();
        }
    }

    // ---------------------------------------------------------------
    // Criar um novo timer: página aberta pelo carrossel (← ← no contador).
    // No carrossel ela fica à esquerda dos timers: → → volta para o contador.
    // ---------------------------------------------------------------
    class NewTimerPage : Border
    {
        // hint: dica já montada por quem abriu a página (a tela cheia usa uma do tamanho da tela);
        // sem ela, a dica fica por cima da própria página (mini pop-up)
        public NewTimerPage(Action<EventItem> onCreate, Action onCancel, CarouselHint? hint = null)
        {
            var panel = new StackPanel { Width = 240, Margin = new Thickness(16) };

            var header = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            var close = Theme.Icon(Theme.GlyphClose, "Voltar (Esc)", onCancel);
            DockPanel.SetDock(close, Dock.Right);
            header.Children.Add(close);
            var heading = Theme.Text("NOVO TIMER", 12, "Accent", FontWeights.SemiBold);
            heading.VerticalAlignment = VerticalAlignment.Center;
            header.Children.Add(heading);

            var form = new EventForm();
            form.Saved += (name, target) => onCreate(new EventItem { Name = name, Target = target });

            panel.Children.Add(header);
            panel.Children.Add(form);
            var carousel = hint ?? new CarouselHint(panel, 1);
            Child = hint == null ? carousel : panel;
            Loaded += (_, _) => form.FocusName();

            // → → volta para o contador. Num campo de texto, a seta só conta com o cursor no fim
            // (ali ela não moveria o cursor); qualquer outra tecla desfaz o 1º toque
            PreviewKeyDown += (_, e) =>
            {
                if (e.Key != Key.Right)
                {
                    carousel.Cancel();
                    return;
                }
                if (e.IsRepeat) return;
                if (Keyboard.FocusedElement is TextBox box && (box.CaretIndex < box.Text.Length || box.SelectionLength > 0)) return;
                e.Handled = true;
                if (carousel.Press(Key.Right, "Aperte → de novo para voltar ao timer")) onCancel();
            };
        }
    }

    // ---------------------------------------------------------------
    // Configurações: página que entra no lugar do conteúdo atual (mini pop-up ou tela cheia).
    // Menu lateral com tópicos e, à direita, o conteúdo do tópico escolhido.
    // Tudo aplica e salva na hora, inclusive enquanto você digita.
    // ---------------------------------------------------------------
    class SettingsPage : Grid
    {
        readonly AppConfig cfg;
        readonly Action onChanged;              // avisa a janela para se atualizar
        readonly List<Border> navItems = new List<Border>();
        readonly UIElement[] sections;
        readonly Border contentHost = new Border();
        readonly StackPanel eventList = new StackPanel { Margin = new Thickness(0, 0, 10, 0) };
        readonly ScrollViewer eventScroll;
        readonly List<(TextBlock Text, EventItem Event)> timers = new List<(TextBlock, EventItem)>();
        readonly DispatcherTimer clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        int selected = -1;
        int editing = -1;       // evento aberto para edição (-1 = nenhum)
        bool focusEditor;       // ao criar um evento novo, já põe o cursor no nome

        public SettingsPage(AppConfig config, Action changed, Action back)
        {
            cfg = config;
            onChanged = changed;
            Width = 560;    // tamanho fixo: trocar de tópico não faz a janela pular
            Height = 380;

            eventScroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = eventList,
            };
            sections = new[] { EventsSection(), AppearanceSection(), SoundSection(), SystemSection() };

            // --- Menu lateral: voltar + título, depois os tópicos ---
            var backIcon = Theme.Icon(Theme.GlyphBack, "Voltar (Esc)", back, 12);
            var navHeading = Theme.Text("CONFIGURAÇÕES", 10, "Label", FontWeights.SemiBold);
            navHeading.VerticalAlignment = VerticalAlignment.Center;
            navHeading.Margin = new Thickness(4, 0, 0, 0);
            var top = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 0, 0, 10) };
            top.Children.Add(backIcon);
            top.Children.Add(navHeading);

            var nav = new StackPanel { Margin = new Thickness(10, 12, 10, 10) };
            nav.Children.Add(top);
            nav.Children.Add(NavItem(0, Theme.GlyphCalendar, "Eventos"));
            nav.Children.Add(NavItem(1, Theme.GlyphPalette, "Aparência"));
            nav.Children.Add(NavItem(2, Theme.GlyphVolume, "Som"));
            nav.Children.Add(NavItem(3, Theme.GlyphSystem, "Sistema"));
            var sidebar = new Border { CornerRadius = new CornerRadius(13, 0, 0, 13), Child = nav };
            sidebar.SetResourceReference(Border.BackgroundProperty, "SidebarBack");

            Grid.SetColumn(contentHost, 1);
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
            ColumnDefinitions.Add(new ColumnDefinition());
            Children.Add(sidebar);
            Children.Add(contentHost);

            // Os cronômetros de cada evento andam a cada segundo enquanto a página está aberta
            clock.Tick += (_, _) => UpdateTimers();
            Loaded += (_, _) => clock.Start();
            Unloaded += (_, _) => clock.Stop();

            Select(0);
        }

        // Item do menu: ícone + nome; o escolhido fica com fundo destacado
        Border NavItem(int index, string glyph, string text)
        {
            var icon = new TextBlock
            {
                Text = glyph,
                FontFamily = Theme.Icons,
                FontSize = 14,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0),
            };
            var label = Theme.Crisp(new TextBlock
            {
                Text = text,
                FontFamily = Theme.Ui,
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center,
            });
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(icon);
            row.Children.Add(label);

            var item = new Border
            {
                CornerRadius = new CornerRadius(7),
                Padding = new Thickness(10, 7, 10, 7),
                Margin = new Thickness(0, 0, 0, 2),
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand,
                Child = row,
            };
            item.MouseEnter += (_, _) => { if (index != selected) item.SetResourceReference(Border.BackgroundProperty, "NavHover"); };
            item.MouseLeave += (_, _) => { if (index != selected) item.Background = Brushes.Transparent; };
            item.MouseLeftButtonDown += (_, e) => e.Handled = true;
            item.MouseLeftButtonUp += (_, e) => { e.Handled = true; Select(index); };
            navItems.Add(item);
            return Theme.Pressable(item, 0.95);
        }

        void Select(int index)
        {
            if (index == selected) return;
            selected = index;
            for (int i = 0; i < navItems.Count; i++)
            {
                bool on = i == index;
                if (on) navItems[i].SetResourceReference(Border.BackgroundProperty, "NavSelected");
                else navItems[i].Background = Brushes.Transparent;
                // TextElement.Foreground é herdado: pinta o ícone e o texto do item de uma vez
                navItems[i].SetResourceReference(TextElement.ForegroundProperty, on ? "Title" : "Label");
            }
            contentHost.Child = sections[index];
            Theme.SlideIn(sections[index]);   // o tópico entra deslizando
        }

        // Salva no JSON, ajusta o som e avisa a janela
        void Changed()
        {
            Storage.Save(cfg);
            ClockSound.Apply(cfg);
            onChanged();
        }

        // --- Tópico: Eventos ---

        UIElement EventsSection()
        {
            var heading = Theme.Text("Eventos", 17, "Title", FontWeights.SemiBold);
            heading.VerticalAlignment = VerticalAlignment.Center;
            var add = Theme.Button("+ NOVO EVENTO", NewEvent, compact: true);
            var header = new DockPanel { Margin = new Thickness(0, 0, 10, 12) };
            DockPanel.SetDock(add, Dock.Right);
            header.Children.Add(add);
            header.Children.Add(heading);

            var grid = new Grid { Margin = new Thickness(24, 16, 14, 12) };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition());
            Grid.SetRow(eventScroll, 1);
            grid.Children.Add(header);
            grid.Children.Add(eventScroll);
            RebuildEvents();
            return grid;
        }

        // Remonta a lista de cartões de evento
        List<Border> RebuildEvents()
        {
            eventList.Children.Clear();
            timers.Clear();
            var cards = new List<Border>();
            for (int i = 0; i < cfg.Events.Count; i++)
            {
                var card = EventCard(i);
                cards.Add(card);
                eventList.Children.Add(card);
            }
            UpdateTimers();
            return cards;
        }

        // Cartão de um evento: nome, cronômetro e, abaixo, "ALTERNAR PARA ESTE" (ou o selo de ativo)
        Border EventCard(int index)
        {
            var ev = cfg.Events[index];
            bool active = index == cfg.ActiveEvent;
            bool isEditing = index == editing;

            var name = Theme.Text(ev.Name.ToUpperInvariant(), 13, "Title", FontWeights.SemiBold);
            name.TextWrapping = TextWrapping.NoWrap;
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            name.VerticalAlignment = VerticalAlignment.Center;

            var icons = new StackPanel { Orientation = Orientation.Horizontal };
            var edit = Theme.Icon(Theme.GlyphEdit, isEditing ? "Concluir edição" : "Editar", () =>
            {
                editing = isEditing ? -1 : index;
                RebuildEvents();
            }, 12);
            if (isEditing) Theme.SetActive(edit, true);
            icons.Children.Add(edit);
            if (cfg.Events.Count > 1) icons.Children.Add(Theme.DeleteButton(() => DeleteEvent(index), 12));   // sempre fica ao menos um

            var header = new DockPanel();
            DockPanel.SetDock(icons, Dock.Right);
            header.Children.Add(icons);
            header.Children.Add(name);

            var timer = new TextBlock { FontFamily = Theme.Digits, FontSize = 22, Margin = new Thickness(0, 2, 0, 8) };
            timer.SetResourceReference(TextBlock.ForegroundProperty, active ? "Accent" : "Title");
            timers.Add((timer, ev));

            var body = new StackPanel();
            body.Children.Add(header);
            body.Children.Add(timer);
            if (isEditing) body.Children.Add(EventEditor(ev, name));

            if (active)
            {
                var badge = Theme.Text("●  EVENTO ATIVO", 11, "Accent", FontWeights.SemiBold);
                badge.Margin = new Thickness(0, 4, 0, 2);
                body.Children.Add(badge);
            }
            else
            {
                var change = Theme.Button("ALTERNAR PARA ESTE", () => Activate(index), compact: true);
                change.HorizontalAlignment = HorizontalAlignment.Left;
                body.Children.Add(change);
            }

            var card = new Border
            {
                CornerRadius = new CornerRadius(10),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(14, 10, 14, 12),
                Margin = new Thickness(0, 0, 0, 8),
                Child = body,
            };
            card.SetResourceReference(Border.BackgroundProperty, "CardRow");
            card.SetResourceReference(Border.BorderBrushProperty, active ? "Accent" : "InputBorder");
            return card;
        }

        // Campos de edição dentro do cartão: salvam sozinhos a cada letra (quando o conteúdo é válido)
        StackPanel EventEditor(EventItem ev, TextBlock nameLabel)
        {
            var nameBox = Theme.Input(ev.Name);
            var dateBox = Theme.Input(DateInput.ToText(ev.Target));
            var status = Theme.Text("As alterações são salvas automaticamente.", 11, "Label");
            status.Margin = new Thickness(0, 6, 0, 0);

            void Status(string text, string colorKey)
            {
                status.Text = text;
                status.SetResourceReference(TextBlock.ForegroundProperty, colorKey);
            }

            void Autosave()
            {
                string name = nameBox.Text.Trim();
                if (name.Length == 0)
                {
                    Status("Digite um nome.", "Error");
                    return;
                }
                if (!DateInput.TryParse(dateBox.Text, out DateTime target))
                {
                    Status("Data incompleta ou inválida (ex.: 25/12/2026 18:30).", "Error");
                    return;
                }
                ev.Name = name;
                ev.Target = target;
                nameLabel.Text = name.ToUpperInvariant();
                Changed();
                UpdateTimers();
                Status("Salvo automaticamente.", "Label");
            }

            // Enter = concluir a edição
            void EnterCloses(object sender, KeyEventArgs e)
            {
                if (e.Key != Key.Enter) return;
                e.Handled = true;
                editing = -1;
                RebuildEvents();
            }

            nameBox.TextChanged += (_, _) => Autosave();
            dateBox.TextChanged += (_, _) => Autosave();
            nameBox.KeyDown += EnterCloses;
            dateBox.KeyDown += EnterCloses;
            if (focusEditor)
            {
                focusEditor = false;
                nameBox.Loaded += (_, _) => { nameBox.Focus(); nameBox.SelectAll(); };
            }

            var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
            panel.Children.Add(Theme.Caption("Nome do evento"));
            panel.Children.Add(nameBox);
            panel.Children.Add(Theme.Caption("Data e hora (dd/mm/aaaa hh:mm)"));
            panel.Children.Add(dateBox);
            panel.Children.Add(status);
            Theme.SlideIn(panel, 0, -6);
            return panel;
        }

        void Activate(int index)
        {
            cfg.ActiveEvent = index;
            editing = -1;
            Changed();
            var cards = RebuildEvents();
            Theme.Pop(cards[index]);   // o cartão escolhido dá um "pulinho"
        }

        // Novo evento: já entra na lista (salvo) e abre para edição
        void NewEvent()
        {
            var now = DateTime.Now;
            cfg.Events.Add(new EventItem { Name = "Novo evento", Target = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0).AddDays(7) });
            editing = cfg.Events.Count - 1;
            focusEditor = true;
            Changed();
            var cards = RebuildEvents();
            Theme.Pop(cards[editing]);
            eventScroll.UpdateLayout();
            eventScroll.ScrollToEnd();
        }

        void DeleteEvent(int index)
        {
            if (cfg.Events.Count <= 1) return;   // sempre fica pelo menos um evento
            cfg.Events.RemoveAt(index);
            if (cfg.ActiveEvent > index) cfg.ActiveEvent--;
            else if (cfg.ActiveEvent == index) cfg.ActiveEvent = Math.Min(index, cfg.Events.Count - 1);
            editing = -1;
            Changed();
            RebuildEvents();
        }

        // Atualiza o cronômetro escrito de cada cartão
        void UpdateTimers()
        {
            foreach (var (text, ev) in timers) text.Text = Describe(ev.Target);
        }

        string Describe(DateTime target)
        {
            TimeSpan left = target - DateTime.Now;
            if (left <= TimeSpan.Zero) return "Chegou o dia!";
            if (cfg.ShowSeconds)
            {
                long s = (long)Math.Ceiling(left.TotalSeconds);
                return $"{s / 86400:00}d  {s % 86400 / 3600:00}h  {s % 3600 / 60:00}m  {s % 60:00}s";
            }
            long m = (long)Math.Ceiling(left.TotalMinutes);
            return $"{m / 1440:00}d  {m % 1440 / 60:00}h  {m % 60:00}m";
        }

        // --- Outros tópicos ---

        UIElement AppearanceSection() => Section("Aparência",
            Row("Modo transparente", "O mini pop-up fica semitransparente e volta ao normal com o mouse em cima.",
                Theme.Switch(cfg.Transparent, on => { cfg.Transparent = on; Changed(); })),
            Row("Mostrar segundos", "Adiciona a plaquinha de segundos ao contador.",
                Theme.Switch(cfg.ShowSeconds, on => { cfg.ShowSeconds = on; Changed(); UpdateTimers(); })));

        UIElement SoundSection()
        {
            var names = new string[ClockSound.Tracks.Length];
            for (int i = 0; i < names.Length; i++) names[i] = ClockSound.Tracks[i].Name;

            // Volume: barra + porcentagem; muda ao vivo, toca uma amostra e salva ao soltar
            var percent = Theme.Text($"{cfg.Volume:P0}", 12, "InputFore", FontWeights.SemiBold);
            percent.Width = 38;
            percent.TextAlignment = TextAlignment.Right;
            percent.VerticalAlignment = VerticalAlignment.Center;
            var slider = Theme.Slider(cfg.Volume,
                v =>
                {
                    cfg.Volume = v;
                    percent.Text = $"{v:P0}";
                    ClockSound.SetVolume(v);
                    ClockSound.Preview(cfg);
                },
                _ => Storage.Save(cfg));
            slider.Width = 150;
            var volume = new StackPanel { Orientation = Orientation.Horizontal };
            volume.Children.Add(slider);
            volume.Children.Add(percent);

            var type = Theme.Segmented(names, Math.Clamp(cfg.SoundIndex, 0, names.Length - 1), index =>
            {
                cfg.SoundIndex = index;
                Changed();
                ClockSound.Preview(cfg);   // ouve o som escolhido
            });
            type.HorizontalAlignment = HorizontalAlignment.Left;

            return Section("Som",
                Row("Som do relógio", "Tique-taque no compasso dos segundos. Só toca com \"Mostrar segundos\" ligado (em Aparência).",
                    Theme.Switch(cfg.Sound, on => { cfg.Sound = on; Changed(); })),
                RowBelow("Tipo de som", "Cozinha: quartzo. Cinema: lento e grave. Clássico: relógio de parede.", type),
                Row("Volume", "Arraste, clique na barra ou use a rodinha do mouse.", volume));
        }

        // Linha de opção com o controle embaixo do texto (para controles largos)
        static StackPanel RowBelow(string title, string description, FrameworkElement control)
        {
            var panel = new StackPanel { Margin = new Thickness(0, 10, 0, 10) };
            panel.Children.Add(Theme.Text(title, 13, "InputFore"));
            var desc = Theme.Text(description, 11, "Label");
            desc.Margin = new Thickness(0, 2, 0, 8);
            panel.Children.Add(desc);
            panel.Children.Add(control);
            return panel;
        }

        UIElement SystemSection()
        {
            var row = Row("Iniciar com o Windows", "Abre o Countdown sozinho quando você entra no Windows.", null);
            var description = (TextBlock)((StackPanel)row.Children[0]).Children[1];
            var toggle = Theme.Switch(Startup.IsEnabled(), on =>
            {
                try
                {
                    Startup.Set(on);
                    description.Text = "Abre o Countdown sozinho quando você entra no Windows.";
                    description.SetResourceReference(TextBlock.ForegroundProperty, "Label");
                }
                catch (Exception ex)
                {
                    description.Text = "Não foi possível alterar: " + ex.Message;
                    description.SetResourceReference(TextBlock.ForegroundProperty, "Error");
                }
            });
            toggle.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(toggle, 1);
            row.Children.Add(toggle);
            return Section("Sistema", row);
        }

        // Título do tópico + linhas separadas por um fio
        static StackPanel Section(string title, params UIElement[] rows)
        {
            var panel = new StackPanel { Margin = new Thickness(24, 16, 24, 16) };
            var heading = Theme.Text(title, 17, "Title", FontWeights.SemiBold);
            heading.Margin = new Thickness(0, 0, 0, 8);
            panel.Children.Add(heading);
            for (int i = 0; i < rows.Length; i++)
            {
                if (i > 0)
                {
                    var line = new Border { Height = 1 };
                    line.SetResourceReference(Border.BackgroundProperty, "RowLine");
                    panel.Children.Add(line);
                }
                panel.Children.Add(rows[i]);
            }
            return panel;
        }

        // Linha de opção: nome e descrição à esquerda, controle (chave/botão) à direita
        static Grid Row(string title, string description, UIElement? control)
        {
            var grid = new Grid { Margin = new Thickness(0, 10, 0, 10) };
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
            texts.Children.Add(Theme.Text(title, 13, "InputFore"));
            var desc = Theme.Text(description, 11, "Label");
            desc.Margin = new Thickness(0, 2, 0, 0);
            texts.Children.Add(desc);
            grid.Children.Add(texts);

            if (control is FrameworkElement element)
            {
                element.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(element, 1);
                grid.Children.Add(element);
            }
            return grid;
        }
    }

    // ---------------------------------------------------------------
    // Ponto de entrada do programa
    // ---------------------------------------------------------------
    static class Program
    {
        // Trava de "uma cópia só": duas cópias abertas poderiam salvar uma por cima da outra e perder dados
        static Mutex? singleInstance;
        const string MutexName = @"Local\Countdown.UmaCopiaSo";
        const string ShowSignalName = @"Local\Countdown.Mostrar";

        [STAThread]   // WPF exige que a interface rode numa thread STA
        static void Main()
        {
            singleInstance = new Mutex(true, MutexName, out bool isFirst);
            if (!isFirst)
            {
                // Já tem uma cópia aberta: só pede para ela aparecer e sai
                try
                {
                    using var signal = EventWaitHandle.OpenExisting(ShowSignalName);
                    signal.Set();
                }
                catch { }
                return;
            }
            var showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowSignalName);

            // OnExplicitShutdown: fechar a configuração inicial ou a tela cheia não encerra o app
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };

            AppConfig? cfg = Storage.Load();
            Theme.Apply(cfg?.DarkMode ?? true);   // aplica o tema salvo antes de abrir qualquer janela

            // Primeira vez (sem dados): pede o primeiro evento antes de mostrar o contador
            if (cfg == null)
            {
                var dlg = new FirstRunWindow();
                dlg.ShowDialog();
                if (dlg.Result == null) return;   // cancelou: sai sem fazer nada
                cfg = dlg.Result;
                Storage.Save(cfg);
            }

            var window = new CountdownWindow(cfg);
            window.Closed += (_, _) => app.Shutdown();   // fechar o mini pop-up encerra o programa

            // Fica esperando o aviso de outra cópia; quando chega, mostra a janela que já está aberta
            var waiter = new Thread(() =>
            {
                while (showSignal.WaitOne())
                    app.Dispatcher.BeginInvoke(new Action(window.BringToFront));
            })
            { IsBackground = true };
            waiter.Start();

            app.Run(window);
            GC.KeepAlive(singleInstance);
        }
    }
}
