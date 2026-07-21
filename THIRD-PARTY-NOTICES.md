# Third-party notices

NextRDP depends on third-party software. Those components are not relicensed under the NextRDP Apache-2.0 licence; each remains subject to its upstream licence.

The resolved dependency audit performed for this source launch found:

| Ecosystem | Resolved package/version pairs | Licence metadata |
| --- | ---: | --- |
| NuGet | 96 | 65 MIT, 28 Apache-2.0, 2 PostgreSQL, 1 xUnit legacy licence URL; no unknown entries |
| npm | 206 | 186 MIT, 6 Apache-2.0, 3 ISC, 2 BSD-2-Clause, 2 BSD-3-Clause, 2 MIT-0, 2 MPL-2.0, and one each 0BSD, BlueOak-1.0.0, CC0-1.0; no unknown entries |

Exact versions are recorded in the project files and `src/NextRdp.Admin/package-lock.json`. Dependency metadata should be re-audited whenever those files change.

## Principal runtime projects

| Project | Use | Licence |
| --- | --- | --- |
| [.NET and ASP.NET Core](https://github.com/dotnet) | Runtime, WPF application foundations, web control plane, configuration, logging, and data libraries | MIT |
| [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) | Desktop MVVM helpers | MIT |
| [Entity Framework Core](https://github.com/dotnet/efcore) | SQLite and PostgreSQL persistence | MIT |
| [SQLitePCLRaw](https://github.com/ericsink/SQLitePCL.raw) | Windows SQLite provider integration | Apache-2.0 |
| [Npgsql](https://github.com/npgsql/npgsql) | PostgreSQL .NET provider and EF Core integration | PostgreSQL License |
| [OpenIddict](https://github.com/openiddict/openiddict-core) | OIDC/OAuth product-token and federation foundations | Apache-2.0 |
| [OpenTelemetry .NET](https://github.com/open-telemetry/opentelemetry-dotnet) | Metrics and tracing | Apache-2.0 |
| [React](https://github.com/facebook/react) | Administration-console UI runtime | MIT |
| [Fluent UI React](https://github.com/microsoft/fluentui) | Administration-console components and styling | MIT |
| [TypeScript](https://github.com/microsoft/TypeScript) | Administration-console compilation | Apache-2.0 |
| [Vite](https://github.com/vitejs/vite) and [Vitest](https://github.com/vitest-dev/vitest) | Frontend build and tests | MIT |
| [lightningcss](https://github.com/parcel-bundler/lightningcss) | Frontend CSS build processing | MPL-2.0 |
| [xUnit.net](https://github.com/xunit/xunit) | .NET tests | Apache-2.0 and the legacy xUnit licence for `xunit.abstractions` |

## Container images and services

The Compose deployment pulls, rather than vendors, the following principal images. Their images may include additional operating-system packages and notices supplied by their maintainers.

| Image/project | Licence |
| --- | --- |
| [PostgreSQL](https://www.postgresql.org/about/licence/) | PostgreSQL License |
| [OpenFGA](https://github.com/openfga/openfga) | Apache-2.0 |
| [Open Policy Agent](https://github.com/open-policy-agent/opa) | Apache-2.0 |
| [Microsoft .NET container images](https://github.com/dotnet/dotnet-docker) | MIT for the .NET project; base-image components retain their own terms |
| [Node.js container images](https://github.com/nodejs/docker-node) | Node.js and base-image components retain their upstream terms |
| [nginx](https://nginx.org/LICENSE) | 2-clause BSD-style licence |

## Windows components

NextRDP hosts the Microsoft Remote Desktop ActiveX control installed with Windows. The repository does not redistribute `mstscax.dll`. Build-time interop assemblies are generated locally from the installed control and remain ignored build output.

## Distribution note

Source and binary redistributors are responsible for satisfying all applicable upstream attribution, source-availability, and notice requirements. The NextRDP self-contained publish and MSIX include this file together with the project licence and notice. Container images include a copy under `/licenses`; upstream image notices remain inside their respective layers.
