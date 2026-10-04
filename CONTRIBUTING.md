# Contributing

Open an issue describing the device, OS, Unity version, expected result and observed result. For code changes, branch from `main`, keep the change focused, and include verification and any remaining live-device limits.

Never attach API keys, SDK tokens, pairing JSON, Noise URLs carrying identifiers, raw speech, private chat contents, or unredacted phone/Editor logs. The sample's safe diagnostic strings contain only phase names, counts and fixed decision codes.

Protocol changes should include a small synthetic regression fixture. Keep the upstream source revision and license attribution. Native changes need the corresponding macOS or Android compilation check; isolated tests are not a substitute for device acceptance.

See DEVELOPMENT.md for the independent test runner. No commercial host-project assets or configuration belong in this repository.
