# Componentes do frontend

**Objetivo:** explicar telas, estado e ponte React Native–WebView. **Nível:** componentes. **Baseline:** frontend `c69a92e`.

```mermaid
flowchart TB
  ROUTER[Expo Router] --> SCREENS[Telas\nMapa, Linhas, Rotinas placeholder, Configurações]
  SCREENS --> HOOKS[Hooks próprios\nuseMobilidadeRio, useRailRealtime, useTema]
  HOOKS --> SERVICES[Services HTTP e adapters]
  SERVICES --> API[NoPonto API]
  SERVICES --> SIG[SignalR client]
  HOOKS --> STATE[Estado React]
  STATE --> STORE[(AsyncStorage local)]
  STATE --> WV[MapaOSM WebView]
  WV -->|updateMap| GEO[fontes e geometrias]
  WV -->|updateRealtime| VEH[veículos]
  WV -->|updateUser| USER[localização]
  GEO --> ML[MapLibre GL JS]
  VEH --> ML
  USER --> ML
```

React Native cria a WebView e envia atualizações incrementais; o script mantém sources/layers/animação. SignalR atende rodoviário; polling atende ferrovia. TanStack Query está instalado, mas não participa dos fluxos vigentes. AsyncStorage não sincroniza entre dispositivos.

**Fonte:** `app/`, hooks/services e WebView. **Limitações:** Rotinas é placeholder; POIs são incompletos; build distribuído não verificado. **Uso:** capítulo mobile e separação dos dois runtimes.
