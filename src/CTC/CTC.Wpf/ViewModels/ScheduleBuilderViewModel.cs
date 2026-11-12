using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows.Input;
using CTC.Core.Interfaces;
using CTC.Core.Models;
using CTC.Core.Scheduling;
using CTC.Wpf.Commands;
using Microsoft.Win32;

namespace CTC.Wpf.ViewModels;

/// <summary>
/// Schedule Builder tab. Generates a spreadsheet-shaped schedule template for the selected
/// line, lets the dispatcher fill in times, and hands the result to CTC to queue.
/// Template building and validation live in CTC.Core (<see cref="ScheduleTemplateFactory"/>,
/// <see cref="ScheduleTemplateConverter"/>); CTC state is changed only through <see cref="ICTCService"/>.
/// </summary>
public class ScheduleBuilderViewModel : ViewModelBase
{
    private readonly ICTCService _ctc;
    private CtcLineState? _line;
    private ScheduleTemplate? _template;
    private string _numberOfTrainsText = "3";
    private string _statusMessage = "Enter the number of trains and generate a schedule template.";

    public ScheduleBuilderViewModel(ICTCService ctc)
    {
        _ctc = ctc ?? throw new ArgumentNullException(nameof(ctc));

        GenerateTemplateCommand = new RelayCommand(_ => GenerateTemplate(), _ => Line is not null);
        QueueScheduleCommand = new RelayCommand(_ => QueueSchedule(), _ => _template is not null);
        UploadScheduleCommand = new RelayCommand(_ => UploadSchedule(), _ => Line is not null);
        ClearTemplateCommand = new RelayCommand(_ => ClearTemplateAndReport(), _ => _template is not null);
    }

    /// <summary>
    /// The line schedules are built for; set by <see cref="MainWindowViewModel"/> from the
    /// global Current Line selection. Changing it discards the current template, because
    /// that template was generated from the previous line's layout.
    /// </summary>
    public CtcLineState? Line
    {
        get => _line;
        set
        {
            if (!SetProperty(ref _line, value))
            {
                return;
            }

            OnPropertyChanged(nameof(LineName));
            OnPropertyChanged(nameof(YardEntryNotice));
            OnPropertyChanged(nameof(HasNoYardEntries));

            if (_template is not null)
            {
                ClearTemplate();
                StatusMessage = "The line or its track layout changed. Generate a new schedule template.";
            }
        }
    }

    public string LineName => Line?.Name ?? "No line selected";

    /// <summary>
    /// The line's permitted starting blocks (its yard entries), or why it cannot be scheduled when
    /// its track layout states none. Empty when no line is selected.
    /// </summary>
    public string YardEntryNotice
    {
        get
        {
            if (Line is null)
            {
                return string.Empty;
            }

            var entries = YardEntries.For(Line);
            return entries.Count == 0
                ? YardEntries.NoneConfiguredMessage(Line)
                : $"Starting blocks (yard entries): {string.Join(", ", entries.Select(YardEntries.Label))}";
        }
    }

    /// <summary>The selected line's layout has no yard entries, so nothing can be scheduled on it.</summary>
    public bool HasNoYardEntries => Line is not null && YardEntries.For(Line).Count == 0;

    /// <summary>One starting-block selector per train of the current template, in column order.</summary>
    public ObservableCollection<TrainStartSelectionViewModel> TrainStarts { get; } = new ObservableCollection<TrainStartSelectionViewModel>();

    public string NumberOfTrainsText
    {
        get => _numberOfTrainsText;
        set => SetProperty(ref _numberOfTrainsText, value);
    }

    /// <summary>Validation errors and outcomes of the last action.</summary>
    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    /// <summary>Train IDs of the template's time columns; the grid creates one column per entry.</summary>
    public ObservableCollection<string> TrainColumns { get; } = new ObservableCollection<string>();

    public ObservableCollection<ScheduleTemplateRowViewModel> Rows { get; } = new ObservableCollection<ScheduleTemplateRowViewModel>();

    public ICommand GenerateTemplateCommand { get; }

    public ICommand QueueScheduleCommand { get; }

    public ICommand UploadScheduleCommand { get; }

    public ICommand ClearTemplateCommand { get; }

    private void GenerateTemplate()
    {
        if (Line is null)
        {
            StatusMessage = "Select a line before generating a schedule template.";
            return;
        }

        if (!int.TryParse(NumberOfTrainsText?.Trim(), out int trainCount) || trainCount <= 0)
        {
            StatusMessage = "Number of Trains must be a positive whole number.";
            return;
        }

        if (Line.Blocks.Count == 0)
        {
            StatusMessage = $"{Line.Name} has no blocks to schedule.";
            return;
        }

        if (HasNoYardEntries)
        {
            StatusMessage = $"Schedule template not generated: {YardEntries.NoneConfiguredMessage(Line)}";
            return;
        }

        // Supported IDs (001-010) no scheduled, queued or dispatched train uses on any line, so a
        // new template never reuses a train ID. Too few left is reported, never thrown.
        try
        {
            ShowTemplate(ScheduleTemplateFactory.Create(Line, TrainIds.Allocate(_ctc.State, trainCount)));
        }
        catch (InvalidOperationException ex)
        {
            StatusMessage = $"Schedule template not generated: {ex.Message}";
            return;
        }

        StatusMessage = $"Generated a {trainCount}-train template for {Line.Name}. "
            + StartInstruction()
            + $"Enter each train's departure time in its starting block and the time it enters any later blocks (e.g. stations) as {ScheduleTemplateConverter.TimeFormat}; "
            + "CTC routes the train through the blank blocks in between. "
            + "Each timed stop gets a default Dwell (45 s at a station, otherwise 0 s), which you can change.";
    }

    private void QueueSchedule()
    {
        if (_template is null)
        {
            StatusMessage = "Generate a schedule template first.";
            return;
        }

        if (Line is null)
        {
            StatusMessage = "Select a line before queueing a schedule.";
            return;
        }

        var result = ScheduleTemplateConverter.Convert(_template);
        if (!result.IsSuccess)
        {
            StatusMessage = $"Schedule not queued: {result.ErrorMessage}";
            return;
        }

        try
        {
            _ctc.QueueSchedule(result.Trains);
            // Show the routes CTC worked out, so the dispatcher can check the blank blocks were filled in as intended.
            var routes = result.Trains.Select(train => $"{TrainIds.DisplayName(train.TrainId)}: {string.Join(" → ", train.Route.Select(block => block.BlockId))}");
            StatusMessage = $"Queued {result.Trains.Count} train(s) for {Line.Name}. {string.Join("; ", routes)}";

            // CTC's routes follow the layout's direction of travel and switch geometry; warn if one would still be refused.
            var problems = result.Trains.SelectMany(train => RouteGeometry.FindProblems(Line, train)).ToList();
            if (problems.Count > 0)
            {
                StatusMessage += $" Warning: {string.Join(" ", problems)}";
            }
        }
        catch (ArgumentException ex)
        {
            StatusMessage = $"Schedule not queued: {ex.Message}";
        }
    }

    /// <summary>
    /// Loads a CSV schedule into the builder for the dispatcher to review. It is NOT queued:
    /// the dispatcher still presses Queue Schedule, which validates it like a manual schedule.
    /// The current template is kept if the dialog is canceled or the file is rejected.
    /// </summary>
    private void UploadSchedule()
    {
        if (Line is null)
        {
            StatusMessage = "Select a line before uploading a schedule.";
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "Upload Schedule",
            Filter = "CSV Schedule (*.csv)|*.csv|All files (*.*)|*.*",
            CheckFileExists = true,
        };

        if (HasNoYardEntries)
        {
            StatusMessage = $"Schedule upload failed: {YardEntries.NoneConfiguredMessage(Line)}";
            return;
        }

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        string fileName = Path.GetFileName(dialog.FileName);
        string csv;
        try
        {
            csv = File.ReadAllText(dialog.FileName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusMessage = $"Schedule upload failed: could not read {fileName}. {ex.Message}";
            return;
        }

        LoadCsv(csv, fileName);
    }

    /// <summary>
    /// Shows an imported CSV schedule for review (the file-dialog-free part of Upload Schedule).
    /// Trains whose starting block the file does not determine are listed for the dispatcher to choose.
    /// </summary>
    public void LoadCsv(string csv, string fileName)
    {
        if (Line is null)
        {
            StatusMessage = "Select a line before uploading a schedule.";
            return;
        }

        // The same IDs a generated template would get, so an imported schedule never reuses a train ID.
        var result = ScheduleCsvImporter.Import(Line, csv, TrainIds.AvailableIds(_ctc.State));
        if (!result.IsSuccess)
        {
            StatusMessage = $"Schedule upload failed: {result.ErrorMessage}";
            return;
        }

        var template = result.Template!;
        ShowTemplate(template);
        NumberOfTrainsText = template.TrainIds.Count.ToString(CultureInfo.InvariantCulture);
        string starts = result.TrainsNeedingStartSelection.Count == 0
            ? string.Empty
            : $"Choose a starting block for {string.Join(", ", result.TrainsNeedingStartSelection.Select(TrainIds.DisplayName))}: the file does not determine it. ";
        StatusMessage = $"Loaded {template.TrainIds.Count}-train schedule for {Line.Name} from {fileName}. "
            + starts
            + "Review the schedule, then press Queue Schedule.";
    }

    /// <summary>
    /// Called after CTC applied a new track layout. A template whose line's yard entries or blocks
    /// changed was built from an obsolete layout (its starting-block choices may no longer be
    /// permitted), so it is discarded rather than queued from stale data.
    /// </summary>
    public void OnTrackLayoutChanged()
    {
        OnPropertyChanged(nameof(YardEntryNotice));
        OnPropertyChanged(nameof(HasNoYardEntries));
        if (_template is null || Line is null)
        {
            return;
        }

        bool sameEntries = YardEntries.For(Line).Select(block => block.BlockId).SequenceEqual(_template.YardEntryBlockIds);
        bool sameBlocks = Line.Blocks.Select(block => block.BlockId).SequenceEqual(_template.Rows.Select(row => row.BlockId));
        if (!sameEntries || !sameBlocks)
        {
            ClearTemplate();
            string what = sameEntries ? "its blocks" : "its yard entries";
            StatusMessage = $"{Line.Name}: the track layout changed ({what}), so the template's starting blocks may no longer be valid. Generate a new schedule template.";
        }
    }

    private string StartInstruction()
    {
        if (_template is null)
        {
            return string.Empty;
        }

        return _template.YardEntryBlockIds.Count == 1
            ? $"Every train starts at the line's only yard entry, {_template.DescribeYardEntries()}. "
            : $"Choose each train's starting block (yard entries: {_template.DescribeYardEntries()}). ";
    }

    private void OnStartBlockChanged(int column, StartBlockChange change)
    {
        foreach (var row in Rows)
        {
            row.TrainTimes[column].Refresh();
        }

        if (change.Message is not null)
        {
            StatusMessage = change.Message;
        }
    }

    /// <summary>
    /// Discards only the template being edited. The track layout, queued schedule,
    /// dispatched trains and system time live in CTC and are left untouched.
    /// </summary>
    private void ClearTemplateAndReport()
    {
        ClearTemplate();
        StatusMessage = "Schedule template cleared. Enter the number of trains and generate a new template.";
    }

    private void ShowTemplate(ScheduleTemplate template)
    {
        ClearTemplate();

        _template = template;

        // Columns before rows, so the grid's columns exist when the rows' cells are bound.
        foreach (var trainId in template.TrainIds)
        {
            TrainColumns.Add(trainId);
        }

        foreach (var row in template.Rows)
        {
            Rows.Add(new ScheduleTemplateRowViewModel(template, row, error => StatusMessage = error));
        }

        for (int column = 0; column < template.TrainIds.Count; column++)
        {
            TrainStarts.Add(new TrainStartSelectionViewModel(template, column, OnStartBlockChanged));
        }
    }

    private void ClearTemplate()
    {
        _template = null;
        Rows.Clear();
        TrainColumns.Clear();
        TrainStarts.Clear();
    }
}
