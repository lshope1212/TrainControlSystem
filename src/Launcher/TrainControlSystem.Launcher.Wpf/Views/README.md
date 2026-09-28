# Views

Future `UserControl` views for the launcher go here.

The single application window is `MainWindow.xaml` in the project root.

**Do not** add subsystem UIs here. `TrainModel.Wpf`, `TrackModel.Wpf`,
`TrainController.Wpf`, `TrackController.Wpf`, and `CTC.Wpf` are independent
executables that the launcher starts as separate processes — they are deliberately
*not* hosted inside the launcher window.
