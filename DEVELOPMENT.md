# Development

Requires Unity 6 and Python 3. For native builds, use Xcode Command Line Tools on macOS and an Android SDK/JDK for Android.

## Isolated Unity tests

```sh
git clone https://github.com/facebookincubator/muse-gadget-sdk.git
cd muse-gadget-sdk
git checkout 693cde9a884ad1edc87251b9f8944815f8de4809
cd ..
python3 -m venv .venv
.venv/bin/pip install -r muse-unity/Tools~/requirements.txt
.venv/bin/python muse-unity/Tools~/verify.py \
  --unity /path/to/Unity \
  --sdk /path/to/muse-gadget-sdk \
  --python /path/to/.venv/bin/python \
  --output /path/to/disposable-test-project
```

The runner uses Unity's Package Manager API to install the local package into a disposable project, imports Unity-owned TMP resources, and runs synthetic protocol/UI tests with a local Python reference peer. The peer is **test-only**, not a runtime bridge. It never uses a real Muse account or speech recording. Output remains outside the repository.

## Native plugins

```sh
python3 Tools~/build_native.py --mac
python3 Tools~/build_native.py --android-sdk /path/to/android-sdk --jdk /path/to/jdk
```

The macOS command rebuilds the included universal plugin. Restart Unity after changing a loaded plugin. Android Java source is compiled by Unity into host builds; the command above checks source/JNI compatibility.

## Upstream revisions

- Meta SDK: `693cde9a884ad1edc87251b9f8944815f8de4809` — https://github.com/facebookincubator/muse-gadget-sdk
- Community TypeScript client: `89a3feaeac8d18f912c33f621e5f59c300b87d36` — https://github.com/wong2/muse-client
- BouncyCastle.Cryptography: `2.6.2`, portable `netstandard2.0` assembly from the official NuGet package.

See NOTICE and Runtime/Plugins/Crypto/LICENSE.md. No code from a private host application is required to build the package.
