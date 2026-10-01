# How to install Ledger

It takes about a minute. You need Windows 10 or 11 (64-bit). Nothing else to install.

## 1. Download the installer

1. On this GitHub page, find **Releases** in the right-hand column and click the latest one
   (for example **Ledger 3.0.3**).
   Or go straight there: **https://github.com/CH-177/ledger/releases/latest**
2. Scroll down to **Assets** and click **`Ledger-Setup-…exe`** to download it.

> Your browser might ask whether to keep the file, because it isn't downloaded often.
> In Edge: click **⋯** next to the download, then **Keep**, then **Show more** → **Keep anyway**.
> In Chrome: click **Keep**.

## 2. Run it

1. Open your **Downloads** folder and double-click **`Ledger-Setup-…exe`**.
2. Windows will probably show a blue box saying **"Windows protected your PC"**.
   This happens because the installer isn't signed by a company Microsoft knows. It isn't a virus warning.
   - Click **More info** (the small link under the message).
   - A **Run anyway** button appears at the bottom. Click it.
3. In the Ledger Setup window, click **Install**, then **Open Ledger**.

That's it. Ledger is now on your desktop and in the Start menu.

## 3. First launch

Ledger asks you to create your first account (enter the balance your bank shows today), then offers
a two-minute tour. Press **F1** on any page, or click **?** in the sidebar, for help.

Your data stays on your PC. Every so often, back it up: **Settings → Data → Back up now…**, and
save the file to a USB drive or a cloud folder.

## Updating and removing

- **Update:** download the newer `Ledger-Setup-…exe` and run it the same way. Your data is kept.
- **Remove:** Windows **Settings → Apps → Installed apps → Ledger → Uninstall**. Your data is kept
  unless you delete it yourself (it's in `%LOCALAPPDATA%\Ledger`).

## No-install option

Download **`Ledger-…-portable.zip`** instead, unzip it anywhere (a USB stick works), and run
`Ledger\Ledger.exe`. It keeps its data in a `Data` folder beside it.
