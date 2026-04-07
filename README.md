# NWS Helper

Public UI code and support for NWS Helper - a tool to automatically populate and enhance territory addresses in NWS Desktop... and more! Unofficial utilities for JWs working with NW Scheduler / NWS Desktop / NWS Mobile (NOTE: Not affliated with these). Automatically populate & improve territory addresses & more!

## Features

- Automate populating the addresses in NWS Desktop / NWS Mobile / NW Scheduler / NW Publisher

- Import territories from NWS Desktop / NW Scheduler, auto populate addresses for those, export to addresses file, import to NWS

- Preview CSVs to import, preview resulting addresses on an estimated map

- Optionally backfill missing City, State, PostalCode (ZIP), GPS Coordinates on existing addresses (if addresses file from NWS is imported)

- Optionally normalize City, State, Name fields (Use street abbreviations like St/Ave, Captialize names properly)

- Fill in apartment numbers when an example is provided in existing addresses import

- Populate up to 30 addresses per territory with unlimited territories; Purchase add-on for unlimited addresses after testing with Free version

- Aids setup of OpenAddresses.io account / API access (please donate to this amazing resource at bottom of https://openaddresses.io/)

## Repo

This repository is intended to contain the public GUI, CLI, packaging scripts, release automation, and public-facing documentation. The proprietary `NWSHelper.Core` source stays in the private source-of-truth repository and is consumed here as a versioned binary package.

## Support and security

- See [SUPPORT.md](./SUPPORT.md) for issue routing, support expectations, and release troubleshooting guidance.
- See [SECURITY.md](./SECURITY.md) for how to report vulnerabilities without posting them publicly.

## Development
- See [docs/development.md](./docs/development.md)
