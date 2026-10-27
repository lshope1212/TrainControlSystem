# Views

Reusable view pieces for the Train Controller windows:

- `Controls/KeyValueRow` — "label …… value" status row; value colored by `DisplayTone`.
- `Controls/GuidanceBar` — horizontal station / brake point / authority distance bar.
- `Controls/TextToDisplayValueConverter` — wraps plain text for `KeyValueRow`.

Both windows (`MainWindow.xaml`, `TestWindow.xaml`) live in the project root. They are
PEERS: neither is a child of the other (documented exception to the one-window convention).
All colors and styles live in `Resources/Theme.xaml`.
