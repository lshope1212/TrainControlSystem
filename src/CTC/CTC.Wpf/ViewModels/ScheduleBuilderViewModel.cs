using System.Collections.ObjectModel;
using System.Windows.Input;
using CTC.Core.Interfaces;
using CTC.Core.Models;
using CTC.Core.Scheduling;
using CTC.Wpf.Commands;

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
        UploadScheduleCommand = new RelayCommand(_ => UploadSchedule());
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

            if (_template is not null)
            {
                ClearTemplate();
                StatusMessage = "The line or its track layout changed. Generate a new schedule template.";
            }
        }
    }

    public string LineName => Line?.Name ?? "No line selected";

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

        ShowTemplate(ScheduleTemplateFactory.Create(Line, trainCount));
        StatusMessage = $"Generated a {trainCount}-train template for {Line.Name}. "
            + $"Enter the time each train enters each block on its route as {ScheduleTemplateConverter.TimeFormat}, "
            + "starting at the route start block; leave blocks the train does not use blank.";
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
            StatusMessage = $"Queued {result.Trains.Count} train(s) for {Line.Name}.";
        }
        catch (ArgumentException ex)
        {
            StatusMessage = $"Schedule not queued: {ex.Message}";
        }
    }

    private void UploadSchedule()
    {
        // PLACEHOLDER: spreadsheet import is not implemented yet. It should fill a
        // ScheduleTemplate and go through ScheduleTemplateConverter like the manual builder.
        StatusMessage = "Upload Schedule is not implemented yet.";
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
            Rows.Add(new ScheduleTemplateRowViewModel(template, row));
        }
    }

    private void ClearTemplate()
    {
        _template = null;
        Rows.Clear();
        TrainColumns.Clear();
    }
}
