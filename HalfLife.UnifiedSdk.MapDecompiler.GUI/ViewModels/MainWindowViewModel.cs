using Avalonia.Threading;
using DynamicData;
using HalfLife.UnifiedSdk.MapDecompiler.Jobs;
using ReactiveUI;
using Serilog;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Collections.Specialized;

namespace HalfLife.UnifiedSdk.MapDecompiler.GUI.ViewModels
{
    public class MainWindowViewModel : ViewModelBase
    {
        private CancellationTokenSource _jobCancellationTokenSource = new();

        private Task _jobTask = Task.CompletedTask;

        private readonly MapDecompilerFrontEnd _decompiler = new();

        private readonly ILogger _programLogger;

        private readonly Stopwatch _programStopwatch = new();

        public ICommand ConvertFilesCommand { get; }

        public Interaction<OpenFileViewModel, string[]?> ShowConvertFilesDialog { get; } = new();

        public ICommand QuitCommand { get; }

        public Interaction<Unit, Unit> QuitApplication { get; } = new();

        public ICommand CancelAllCommand { get; }

        public Interaction<CancelAllJobsDialogViewModel, bool> ShowCancelAllJobsDialog { get; } = new();

        public bool CanCancelAllJobs => !_jobTask.IsCompleted;

        public ICommand DecompileAllAgainCommand { get; }

        public bool CanDecompileAllJobsAgain => _jobTask.IsCompleted;

        public ObservableCollection<MapDecompilerJob> Files { get; } = new();

        private int _logIndex;

        public int LogIndex
        {
            get => _logIndex;
            set
            {
                this.RaiseAndSetIfChanged(ref _logIndex, value);

                if (_logIndex == 1 && CurrentJob is null)
                {
                    CurrentJob = Files.FirstOrDefault();
                }
            }
        }

        private string _programOutput = string.Empty;

        public string ProgramOutput
        {
            get => _programOutput;
            set => this.RaiseAndSetIfChanged(ref _programOutput, value);
        }

        private MapDecompilerJob? _currentJob;

        public MapDecompilerJob? CurrentJob
        {
            get => _currentJob;
            set
            {
                this.RaiseAndSetIfChanged(ref _currentJob, value);
                this.RaisePropertyChanged(nameof(CanExecuteDelete));
                this.RaisePropertyChanged(nameof(CanDecompileAgain));

                if (_currentJob is not null)
                {
                    LogIndex = 1;
                }
            }
        }

        public DecompilerOptionsViewModel DecompilerOptions { get; } = new();

        public bool HasJobItems => !_jobTask.IsCompleted;

        public ICommand DeleteCommand { get; }

        public bool CanExecuteDelete => CurrentJob is not null && CurrentJob.Status != MapDecompilerJobStatus.Converting;

        public ICommand DecompileAgainCommand { get; }

        public bool CanDecompileAgain => CurrentJob is not null
            && CurrentJob.Status != MapDecompilerJobStatus.Waiting && CurrentJob.Status != MapDecompilerJobStatus.Converting;

        public MainWindowViewModel()
        {
            _programLogger = new LoggerConfiguration()
                .WriteTo.Sink(new ForwardingSink(message => ProgramOutput += message, "{Message:lj}{NewLine}{Exception}"))
                .MinimumLevel.Information()
                .CreateLogger();

            ConvertFilesCommand = ReactiveCommand.CreateFromTask(async () =>
            {
                var store = new OpenFileViewModel
                {
                    Title = "Convert",
                    Filters = new[]
                    {
                        new FileFilter("Half-Life 1 BSP Files (.bsp)", "bsp"),
                        new FileFilter("All Files", "*")
                    },
                    AllowMultiple = true,
                    Directory = Settings.Default.LastConvertDirectory
                };

                var result = await ShowConvertFilesDialog.Handle(store);

                if (result is null || result.Length == 0)
                {
                    return;
                }

                // Use the directory that the first file is located in.
                if (Path.GetDirectoryName(result[0]) is { } directoryName)
                {
                    Settings.Default.LastConvertDirectory = directoryName;
                }

                var outputDirectory = DecompilerOptions.Settings.OutputDirectory;

                if (outputDirectory.Length == 0)
                {
                    outputDirectory = Directory.GetCurrentDirectory();
                }

                var jobs = result
                    .Select(bspFileName =>
                    {
                        var job = new MapDecompilerJob(bspFileName, outputDirectory);

                        job.MessageReceived += LogMessage;

                        return job;
                    })
                    .Where(j => j.MapFileName.Length > 0)
                    .ToList();

                Files.AddRange(jobs);

                QueueJobs(jobs);
            });

            QuitCommand = ReactiveCommand.Create(async () => await QuitApplication.Handle(new()));

            CancelAllCommand = ReactiveCommand.Create(async () => await CancelAllJobs(), this.WhenAnyValue(x => x.CanCancelAllJobs));

            DecompileAllAgainCommand = ReactiveCommand.Create(() => QueueAllJobsAgain(), this.WhenAnyValue(x => x.CanDecompileAllJobsAgain, x => x.Files.Count,
                (canDecompile, filesCount) => canDecompile && filesCount > 0));

            DeleteCommand = ReactiveCommand.Create(
                () => Files.Remove(CurrentJob!),
                this.WhenAnyValue(x => x.CanExecuteDelete));

            DecompileAgainCommand = ReactiveCommand.Create(
                () => QueueJobAgain(CurrentJob!),
                this.WhenAnyValue(x => x.CanDecompileAgain));
        }

        public async Task<bool> ShouldClose()
        {
            if (!HasJobItems)
            {
                return true;
            }

            return await ShowCancelAllJobsDialog.Handle(new());
        }

        public async Task OnClosing()
        {
            await CancelAllJobs();
        }

        public async Task CancelAllJobs()
        {
            _programLogger.Information("Cancelling all jobs");
            _jobCancellationTokenSource.Cancel();
            await _jobTask;
            _jobTask = Task.CompletedTask;
            _jobCancellationTokenSource = new();
        }

        private static void LogMessage(MapDecompilerJob job, string message)
        {
            // This gets called from another thread so sync it.
            Dispatcher.UIThread.Post(() => job.Output += message);
        }

        private void QueueJobs(List<MapDecompilerJob> jobs)
        {
            // Decompile each map one at a time.
            // Anything that relies on user settings should be created before starting the task to prevent race conditions.
            // Make sure to cache objects in local variables to prevent member variables from being captured.
            var decompilerStrategy = DecompilerStrategies.Strategies.FirstOrDefault(s => s.Name == Settings.Default.DecompilerStrategy)
                ?? DecompilerStrategies.Strategies[0];
            var decompilerOptions = DecompilerOptions.ToOptions();
            var generateWadFile = Settings.Default.GenerateWadFile;
            var cancellationToken = _jobCancellationTokenSource.Token;

            // If we're starting a single job just activate the job log automatically.
            if (_jobTask.IsCompleted && jobs.Count == 1)
            {
                CurrentJob = jobs[0];
            }

            _jobTask = _jobTask.ContinueWith(
                (_, _) => ExecuteJobs(jobs, decompilerStrategy, decompilerOptions, generateWadFile, cancellationToken),
                state: null,
                cancellationToken: CancellationToken.None,
                continuationOptions: TaskContinuationOptions.LongRunning,
                scheduler: TaskScheduler.Default);

            this.RaisePropertyChanged(nameof(CanCancelAllJobs));
            this.RaisePropertyChanged(nameof(CanDecompileAllJobsAgain));
        }

        private static void ResetJob(MapDecompilerJob job)
        {
            job.Output = string.Empty;
            job.MessageReceived += LogMessage;
            job.Status = MapDecompilerJobStatus.Waiting;
        }

        private void QueueJobAgain(MapDecompilerJob job)
        {
            ResetJob(job);
            QueueJobs(new() { job });
        }

        private void QueueAllJobsAgain()
        {
            Debug.Assert(_jobTask.IsCompleted);

            foreach (var job in Files)
            {
                ResetJob(job);
            }

            QueueJobs(Files.ToList());
        }

        private void ExecuteJobs(List<MapDecompilerJob> jobs, DecompilerStrategy decompilerStrategy,
            DecompilerOptions decompilerOptions, bool generateWadFile,
            CancellationToken cancellationToken)
        {
            Dispatcher.UIThread.Post(() => _programLogger.Information("Starting {Count} new jobs", jobs.Count));
            _programStopwatch.Restart();

            try
            {
                Parallel.ForEach(
                    jobs,
                    new ParallelOptions()
                    {
                        CancellationToken = cancellationToken,
                        // Use no more than half the cores to keep the UI responsive.
                        MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2),
                    },
                    job =>
                {
                    Dispatcher.UIThread.Post(() => job.Status = MapDecompilerJobStatus.Converting);

                    var result = _decompiler.Decompile(
                        job, decompilerStrategy, decompilerOptions, generateWadFile, cancellationToken);

                    var timeElapsed = _programStopwatch.Elapsed;

                    Dispatcher.UIThread.Post(() =>
                    {
                        job.Status = result;

                        if (job == CurrentJob)
                        {
                            this.RaisePropertyChanged(nameof(CanDecompileAgain));
                        }

                        _programLogger.Information("{Status}{From} => {To}: Time elapsed: {Time:dd\\.hh\\:mm\\:ss\\.fff}",
                            result != MapDecompilerJobStatus.Done ? $"({result}) " : string.Empty, job.From, job.To, timeElapsed);
                    });
                });
            }
            catch (OperationCanceledException)
            {
                foreach (var job in jobs)
                {
                    if (job.Status == MapDecompilerJobStatus.Waiting)
                    {
                        job.Status = MapDecompilerJobStatus.Canceled;
                    }
                }

                Dispatcher.UIThread.Post(() => this.RaisePropertyChanged(nameof(CanDecompileAgain)));
            }
            catch (Exception e)
            {
                _programLogger.Error(e, "An error occurred while processing one or more jobs");
            }
            finally
            {
                foreach (var job in jobs)
                {
                    job.MessageReceived -= LogMessage;
                }
            }

            {
                var timeElapsed = _programStopwatch.Elapsed;

                Dispatcher.UIThread.Post(() =>
                {
                    _programLogger.Information("Total time elapsed: {Time:dd\\.hh\\:mm\\:ss\\.fff}", timeElapsed);
                    this.RaisePropertyChanged(nameof(CanCancelAllJobs));
                    this.RaisePropertyChanged(nameof(CanDecompileAllJobsAgain));
                });
                _programStopwatch.Stop();
            }
        }
    }
}
