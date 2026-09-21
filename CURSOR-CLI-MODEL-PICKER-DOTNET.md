# Cursor CLI Model Picker — .NET / Windows

این فایل را به Cursor بده و بگو: **طبق همین مشخصات پروژه را بساز و اجرا کن.**

هدف: یک اپ/کتابخانه .NET روی ویندوز که با **Cursor CLI** کار می‌کند، مدل‌های در دسترس را می‌گیرد، با پروب کوتاه مشخص می‌کند الان کدام مدل خلوت‌تر/در دسترس‌تر است، و اگر `resource_exhausted` یا `Unable to reach the model provider` آمد به مدل بعدی سوییچ کند.

مهم: Cursor هیچ API رسمی برای «شلوغی لحظه‌ای مدل» ندارد. این پروژه همان را با CLI و failover می‌سازد.

---

## 0) دستور کار برای Cursor

1. یک solution دات‌نت بساز: `CursorModelPicker.sln`
2. دو پروژه بساز:
   - `src/CursorModelPicker` → class library (`net8.0`)
   - `src/CursorModelPicker.Cli` → console app (`net8.0`) که library را reference کند
3. کد را کامل، قابل کامپایل، با `ImplicitUsings` و `Nullable` بنویس
4. روی ویندوز تست‌پذیر باشد
5. README کوتاه داخل همان ریپو ننویس مگر خواسته شود؛ همه قراردادها همین فایل است
6. از نام باینری `cursor-agent` استفاده کن، نه `agent` (روی ویندوز `agent` ممکن است مال Grok باشد)

---

## 1) پیش‌نیاز محیط

- Windows 10/11
- .NET 8 SDK
- Cursor CLI نصب‌شده

نصب CLI اگر لازم شد:

```powershell
irm 'https://cursor.com/install?win32=true' | iex
```

چک:

```powershell
Get-Command cursor-agent
cursor-agent --version
cursor-agent --list-models
```

احراز هویت یکی از این دو:

```powershell
cursor-agent login
# یا
$env:CURSOR_API_KEY = "<key from cursor.com dashboard>"
```

مسیرهای رایج ویندوز:

- `%LOCALAPPDATA%\cursor-agent\cursor-agent.cmd`
- `%LOCALAPPDATA%\cursor-agent\cursor-agent.exe`
- `%LOCALAPPDATA%\cursor-agent\agent.cmd`

اگر executable نسبی بود، اول این مسیرها را چک کن، بعد PATH.

---

## 2) رفتار مورد نیاز

### 2.1 لیست مدل‌ها

این فرمان‌ها را امتحان کن، هر کدام خروجی داد همان را پارس کن:

```powershell
cursor-agent --list-models
cursor-agent models
```

خروجی معمولاً متنی است شبیه:

```
Available models

auto - Auto
composer-2.5 - Composer 2.5
grok-4.6 - Grok 4.6
```

Parser باید:

- خط‌های هدر مثل `Available models` و `Tip:` را نادیده بگیرد
- از اول هر خط، model id را بردارد (قبل از ` - `)
- id تکراری را حذف کند
- ANSI color را در نظر نگیرد؛ هنگام اجرا `NO_COLOR=1` و `FORCE_COLOR=0` ست شود

اگر لیست خالی شد، از لیست پیش‌فرض زیر استفاده کن:

1. `composer-2.5`
2. `auto`
3. `composer-2`
4. `grok-4.6`

این لیست باید از options قابل تغییر باشد.

### 2.2 پروب خلوتی

برای هر مدل کاندید یک درخواست خیلی کوتاه بزن:

```powershell
cursor-agent -p --mode ask --output-format text --trust --workspace <temp> --model <id> "Reply with exactly: pong"
```

قوانین پروب:

- حتماً `--mode ask` تا فایل پروژه را ویرایش نکند
- `--print` / `-p` برای non-interactive
- timeout پیش‌فرض ۲۵ ثانیه
- حداکثر ۳ پروب موازی
- نتیجه موفق: exit code 0 و متن بدون خطای ظرفیت
- نتیجه مشغول: متن شامل یکی از این‌ها (case-insensitive):
  - `resource_exhausted`
  - `RetriableError`
  - `Unable to reach the model provider`
  - `High Load`
  - `high demand`
  - `ERROR_RESOURCE_EXHAUSTED`
- timeout را هم مشغول حساب کن
- مدل مشغول را ۶۰ ثانیه در حافظه mark کن و دوباره پروب نکن
- نتیجه کل پروب‌ها را ۴۵ ثانیه cache کن

مدل برنده = بین مدل‌های `Ok` آن که کمترین latency را دارد.

### 2.3 اجرای واقعی با failover

برای پرامپت اصلی:

1. اول کاندیدها را بر اساس پروب مرتب کن (Ok سپس latency)
2. مدل‌های busy را رد کن
3. همان فرمان CLI را با پرامپت کاربر اجرا کن
4. اگر خروجی/stderr خطای ظرفیت داشت، مدل را busy کن و مدل بعدی را امتحان کن
5. اگر همه شکست خوردند exception واضح فارسی/انگلیسی بده

برای اجرای واقعی پیش‌فرض هم `--mode ask` باشد مگر caller صریحاً mode دیگری بدهد. یک option به نام `Mode` با مقادیر `ask` | `plan` | `agent` بگذار. پیش‌فرض `ask`.

اگر mode برابر `agent` شد، پرچم‌های `--trust` را نگه دار ولی `--force` را فقط وقتی option `Force=true` است اضافه کن. پیش‌فرض Force=false.

---

## 3) ساختار پروژه

```
CursorModelPicker.sln
src/CursorModelPicker/CursorModelPicker.csproj
src/CursorModelPicker/CursorCliOptions.cs
src/CursorModelPicker/CursorCliClient.cs
src/CursorModelPicker/CursorModelRouter.cs
src/CursorModelPicker/Models.cs
src/CursorModelPicker.Cli/CursorModelPicker.Cli.csproj
src/CursorModelPicker.Cli/Program.cs
```

---

## 4) قرارداد کلاس‌ها

### CursorCliOptions

```csharp
public sealed class CursorCliOptions
{
    public string Executable { get; set; } = "cursor-agent";
    public string? ApiKey { get; set; }
    public string Workspace { get; set; } = Path.GetTempPath();
    public string Mode { get; set; } = "ask"; // ask | plan | agent
    public bool Force { get; set; }
    public IReadOnlyList<string> PreferredModels { get; set; } =
        ["composer-2.5", "auto", "composer-2", "grok-4.6"];
    public TimeSpan ProbeTimeout { get; set; } = TimeSpan.FromSeconds(25);
    public TimeSpan RunTimeout { get; set; } = TimeSpan.FromMinutes(3);
    public TimeSpan BusyTtl { get; set; } = TimeSpan.FromSeconds(60);
    public TimeSpan CacheTtl { get; set; } = TimeSpan.FromSeconds(45);
    public int MaxParallelProbes { get; set; } = 3;
}
```

`ApiKey` اگر null بود از `CURSOR_API_KEY` خوانده شود.

### Models

```csharp
public sealed record CliRunResult(int ExitCode, string StdOut, string StdErr);

public sealed record ProbeResult(
    string Model,
    bool Ok,
    TimeSpan Latency,
    string Status, // ok | busy | timeout | error
    string Detail);
```

### CursorCliClient

متدها:

- `Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct = default)`
- `Task<ProbeResult> ProbeAsync(string model, CancellationToken ct = default)`
- `Task<CliRunResult> RunPromptAsync(string model, string prompt, TimeSpan? timeout = null, CancellationToken ct = default)`
- `static bool IsBusyError(string text)`

اجرای process:

- `UseShellExecute = false`
- stdout/stderr redirect
- `CreateNoWindow = true`
- UTF-8
- env: `NO_COLOR=1`, `FORCE_COLOR=0`, `TERM=dumb`
- اگر executable پسوند `.cmd` یا `.bat` داشت با `cmd.exe /d /s /c "..."` اجرا کن
- روی timeout، `Kill(entireProcessTree: true)`
- argumentها را quote کن

Resolve executable به این ترتیب:

1. اگر path مطلق بود و فایل وجود داشت همان
2. `%LOCALAPPDATA%\cursor-agent\cursor-agent.cmd`
3. `%LOCALAPPDATA%\cursor-agent\cursor-agent.exe`
4. `%LOCALAPPDATA%\cursor-agent\agent.cmd`
5. `%LOCALAPPDATA%\cursor-agent\agent.exe`
6. همان نام خام تا PATH آن را پیدا کند

### CursorModelRouter

متدها:

- `Task<string> PickQuietestAsync(CancellationToken ct = default)`
- `Task<IReadOnlyList<ProbeResult>> ProbeCandidatesAsync(CancellationToken ct = default)`
- `Task<CliRunResult> RunWithFailoverAsync(string prompt, CancellationToken ct = default)`

کاندیدها = `PreferredModels` که یا در لیست CLI هستند یا برابر `auto`اند. اگر هیچ‌کدام نماند، ۴ مدل اول لیست CLI.

Cache و busy-map باید thread-safe باشند (`lock`).

اگر هیچ مدل Ok نبود:

```
هیچ مدلی الان در دسترس نیست. همه High Load یا unreachable بودند.
```

---

## 5) CLI برنامه

پروژه console این فرمان‌ها را داشته باشد:

```powershell
dotnet run --project src/CursorModelPicker.Cli -- probe
dotnet run --project src/CursorModelPicker.Cli -- pick
dotnet run --project src/CursorModelPicker.Cli -- run "Reply with exactly: pong"
dotnet run --project src/CursorModelPicker.Cli -- list
```

خروجی `probe`:

```
OK    composer-2.5             1840 ms  ok     pong
FAIL  grok-4.6                 2100 ms  busy   Unable to reach the model provider
```

خروجی `pick`:

```
quietest: composer-2.5
```

خروجی `list`: یکی در هر خط، فقط model id.

`run`: stdout مدل را چاپ کند. اگر همه fail شدند exit code 2.

اگر آرگومان خالی بود، رفتار پیش‌فرض `probe` سپس `pick` باشد.

---

## 6) نمونه استفاده داخل اپ دیگر

```csharp
var router = new CursorModelRouter(new CursorCliOptions
{
    Executable = "cursor-agent",
    ApiKey = Environment.GetEnvironmentVariable("CURSOR_API_KEY"),
    PreferredModels = ["composer-2.5", "auto", "composer-2", "grok-4.6"],
    Mode = "ask"
});

var model = await router.PickQuietestAsync();
var result = await router.RunWithFailoverAsync("این فانکشن را خلاصه کن");
Console.WriteLine(result.StdOut);
```

---

## 7) تست دستی که بعد از ساخت باید انجام شود

در PowerShell:

```powershell
dotnet build CursorModelPicker.sln
dotnet run --project src/CursorModelPicker.Cli -- list
dotnet run --project src/CursorModelPicker.Cli -- probe
dotnet run --project src/CursorModelPicker.Cli -- pick
```

اگر CLI نصب نبود، برنامه باید پیام واضح بدهد:

```
cursor-agent پیدا نشد. نصب کن: irm 'https://cursor.com/install?win32=true' | iex
بعد PATH را تازه کن و دوباره تلاش کن.
```

این را با catch روی Win32Exception / file not found هندل کن.

---

## 8) چیزهایی که نباید انجام شود

- از فرمان `agent` به عنوان پیش‌فرض استفاده نکن
- پروب را در mode agent و روی ریپوی واقعی کاربر اجرا نکن
- کل کاتالوگ مدل‌ها را همزمان پروب نکن؛ فقط PreferredModels
- JSON ساختگی برای load/capacity نساز؛ Cursor چنین فیلدی ندارد
- API غیررسمی داخلی Cursor را reverse-engineer نکن
- وابستگی NuGet اضافه غیر از BCL نگذار مگر ضروری باشد؛ این پروژه باید بدون پکیج خارجی کار کند

---

## 9) زمینه محصول (برای تصمیم‌گیری Cursor)

خطاهای رایج Cursor که باید تشخیص داده شوند:

- `Error: RetriableError: [resource_exhausted] Error`
- `Unable to reach the model provider`
- `We're having trouble connecting to the model provider. This might be temporary - please try again in a moment.`
- `High Load` / `We're experiencing high demand right now`

این‌ها معمولاً سقف اشتراک نیستند؛ ظرفیت مدل در آن لحظه پر است. مدل را عوض کن، همان conversation را مجبور نکن.

صفحه وضعیت رسمی Cursor فقط وضعیت کلی سرویس را می‌گوید، نه خلوتی هر مدل. پس status.cursor.com را در این نسخه integ نکن مگر به‌صورت اختیاری و جدا.

---

## 10) معیار قبول

پروژه وقتی کامل است که:

- `dotnet build` بدون warning مهم پاس شود
- روی ویندوز `list` و `probe` و `pick` کار کنند
- مدل failشده تا BusyTtl دوباره انتخاب نشود
- `.cmd` با `cmd.exe` درست اجرا شود
- quote کردن path و پرامپت دارای فاصله درست باشد
- اگر یک مدل High Load داد، `run` مدل بعدی را امتحان کند
