using System;
using System.Data.SqlClient;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Extensions.Configuration;

class Program
{
    private static string connectionString = ""; 
    private static readonly string projectRoot = Directory.GetParent(AppContext.BaseDirectory).Parent.Parent.Parent.FullName;         // Start from bin\Debug\net8.0   Go up 3 levels → project root

    static async Task Main()
    {
        // Build configuration
        var builder = new ConfigurationBuilder()
            .SetBasePath(projectRoot)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);

        var configuration = builder.Build();

        connectionString = configuration["DB:ConnectionString"];
        int interval = int.Parse(configuration["Scheduler:CheckIntervalInSeconds"]);

        Console.WriteLine("Test Scheduler started...");

        // Timer setup
        var timer = new System.Timers.Timer(interval * 1000);
        timer.Elapsed += TimerElapsed;
        timer.AutoReset = true;
        timer.Enabled = true;

        Console.WriteLine("Press [Enter] to exit.");
        Console.ReadLine();
    }

    private static async void TimerElapsed(object sender, System.Timers.ElapsedEventArgs e)
    {
        try
        {
            await RunScheduler();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Scheduler error: {ex.Message}");
        }
    }

    private static async Task RunScheduler()
    {
        using var connection = new SqlConnection(connectionString);
        var now = DateTime.Now;

        var schedules = await connection.QueryAsync<Schedule>(
            @"SELECT * FROM TestSchedules
              WHERE IsActive = 1
              AND (FromDate IS NULL OR FromDate <= @Today)
              AND (ToDate IS NULL OR ToDate >= @Today)",
            new { Today = now });

        Console.WriteLine($"Found {schedules.Count()} active schedules at {now}");

        foreach (var schedule in schedules)
        {
            // Split DayOfWeek, remove quotes/spaces
            var days = !string.IsNullOrWhiteSpace(schedule.DaysOfWeek)
                ? schedule.DaysOfWeek
                    .Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(d => d.Trim('\'', ' '))
                    .ToList()
                : Enum.GetNames(typeof(DayOfWeek)).ToList(); // All 7 days if null or empty

            var today = now.DayOfWeek.ToString();
            
            // skip if today is not in DaysOfWeek of Schedule
            if (!days.Contains(today, StringComparer.OrdinalIgnoreCase))
                continue;

            // Parse schedule.AtTime into a DateTime for today
            DateTime? scheduledTime = null;
            if (!string.IsNullOrEmpty(schedule.AtTime))
            {
                if (TimeSpan.TryParse(schedule.AtTime, out var timeOfDay))
                {
                    scheduledTime = now.Date + timeOfDay; // today at AtTime
                }
            }
            // skip if AtTime is null/empty 
            if (!(scheduledTime.HasValue))
                continue;

            // Skip if AtTime is set but now < AtTime (too early)
            if (scheduledTime.HasValue && now < scheduledTime.Value)
                continue;

            // Skip if already run at or after AtTime today
            if (schedule.LastRunTime != null &&
                schedule.LastRunTime.Value >= scheduledTime.Value)
                continue;

            Console.WriteLine($"Running tests for {schedule.ClientName} | Categories: {schedule.TestsToBeRun}");

            await RunTests(schedule.ClientName, schedule.TestsToBeRun);

            // Update LastRunTime
            await connection.ExecuteAsync(
                "UPDATE TestSchedules SET LastRunTime = @Now WHERE Id = @Id",
                new { Now = now, schedule.Id });
        }
    }
    private static Task RunTests(string clientName, string testsToRun)
    {
        // Build configuration
        var builder = new ConfigurationBuilder()
            .SetBasePath(projectRoot)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);
        var configuration = builder.Build();
        string loc = configuration["FilePath"];

        string categories;
        if (string.IsNullOrWhiteSpace(testsToRun) || testsToRun.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            // run all tests for the client
            categories = $"--filter \"TestCategory={clientName}\"";
        }
        else
        {
            var tests = testsToRun
                        .Split(',', StringSplitOptions.RemoveEmptyEntries)
                        .Select(t => t.Trim())
                        .Where(t => t.Length > 0)
                        .Select(t => $"TestCategory={t}");
            // VSTest expects & for AND and | for OR
            categories = $"--filter \"(TestCategory={clientName}) & ({string.Join(" | ", tests)})\"";
        }
        Console.WriteLine($"Executing: dotnet test \"{loc}\" {categories}");

        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"test \"{loc}\" {categories}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        // Pass environment variable
        startInfo.Environment["CLIENT_NAME"] = clientName;

        var process = new Process { StartInfo = startInfo };
        process.OutputDataReceived += (s, e) => { if (e.Data != null) Console.WriteLine(e.Data); };
        process.ErrorDataReceived += (s, e) => { if (e.Data != null) Console.WriteLine($"ERROR: {e.Data}"); };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        return process.WaitForExitAsync();
    }
}

class Schedule
{
    public int Id { get; set; }
    public string ClientName { get; set; }
    public string TestsToBeRun { get; set; }
    public DateTime? FromDate { get; set; }
    public DateTime? ToDate { get; set; }
    public string DaysOfWeek { get; set; }
    public string AtTime { get; set; }
    public DateTime? LastRunTime { get; set; }
    public bool IsActive { get; set; }
}
