# Contributing to Managerr

Thank you for your interest in contributing to Managerr.

## Local Development Setup

### Prerequisites

* .NET 10 SDK
* Node.js 22+ and npm
* Optional runtime tools used by verification tests: `ffmpeg`, `tesseract-ocr` (`eng`, `deu`), `mkvtoolnix`

### Building and Testing

Run the backend build and test suite:

```bash
dotnet build Managerr.sln -c Release -warnaserror -p:AllowMissingPrunePackageData=true
dotnet test Managerr.sln -c Release --no-build -p:AllowMissingPrunePackageData=true
```

Run frontend linting, unit tests, and production bundle build:

```bash
npm --prefix src/Managerr.Frontend ci
npm --prefix src/Managerr.Frontend run lint
npm --prefix src/Managerr.Frontend test -- --run
npm --prefix src/Managerr.Frontend run build
```

Run Playwright E2E smoke tests:

```bash
npx --prefix src/Managerr.Frontend playwright install chromium
npm --prefix src/Managerr.Frontend run test:e2e
```

## Guidelines

* Keep changes focused and include tests for new endpoints, rules, or UI workflows.
* Never commit secrets, personal configuration files, `.env` files, or third-party tracker Cardigann definitions.
* Update [`wiki/`](./wiki/Home.md) when adding or changing user-facing settings, endpoints, or MCP tools.
* Write clear, concise documentation without emojis or promotional filler.

## License

Contributions are licensed under the GNU General Public License v3.0 or later ([`LICENSE`](./LICENSE)).
