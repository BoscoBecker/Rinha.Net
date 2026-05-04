# Rinha Fraud API (.NET)

![Ilustração sobre prevenção de fraude em pagamentos: alertas, transação suspeita e segurança](docs/images/fraude-pagamentos.png)

Implementação em **ASP.NET Core** para a [Rinha de Backend 2026 — Detecção de fraude com busca vetorial](https://github.com/zanfranceschi/rinha-de-backend-2026). O desafio completo está na documentação oficial (por exemplo [docs/br/README.md](https://github.com/zanfranceschi/rinha-de-backend-2026/blob/main/docs/br/README.md)).

## O que este repositório faz

- Expõe **`GET /ready`** e **`POST /fraud-score`** (contrato em [API.md](https://github.com/zanfranceschi/rinha-de-backend-2026/blob/main/docs/br/API.md)).
- Vetoriza cada transação em **14 dimensões**, com as mesmas regras de normalização do desafio ([REGRAS_DE_DETECCAO.md](https://github.com/zanfranceschi/rinha-de-backend-2026/blob/main/docs/br/REGRAS_DE_DETECCAO.md)).
- Carrega o dataset de referência em **formato binário** (`references.bin`, gerado a partir de `references.json.gz`) e faz **k-NN (k = 5)** com distância euclidiana; `fraud_score` e `approved` seguem o enunciado.

## Estrutura da solução

| Caminho | Descrição |
|--------|------------|
| `src/RinhaFraudApi/` | API minimal (Kestrel), vetorização, leitura mmap do `references.bin` |
| `tools/RinhaRefConverter/` | Utilitário que converte `references.json.gz` → `references.bin` (build-time ou local) |
| `Dockerfile` | Build multi-stage: baixa o `references.json.gz` oficial, converte, publica a API `linux-x64` |
| `docker-compose.yml` | **Nginx** (porta 9999) + **2 réplicas** da API + rede `bridge` |
| `nginx.conf` | Proxy para `api1:8080` e `api2:8080` (balanceamento **round-robin** padrão do Nginx) |

## Requisitos

- [.NET 8 SDK](https://dotnet.microsoft.com/download) (para desenvolvimento local).
- [Docker Desktop](https://www.docker.com/products/docker-desktop/) (para subir nginx + APIs como na submissão).

## Execução com Docker (recomendado)

Na raiz do repositório:

```bash
docker compose up --build -d
```

- Entrada pública: **`http://localhost:9999`** (load balancer).
- Cada instância da API escuta **8080** apenas na rede interna do Compose.

### Limites de recursos (regra da Rinha)

No `docker-compose.yml`, a soma dos `deploy.resources.limits` fica dentro do máximo permitido pelo desafio:

- **CPU:** 1,00 no total  
- **RAM:** 342 MB no total (≤ **350 MB**)

*(Em `docker compose up` local, consoante a versão, os limites de `deploy` podem ser ignorados fora de Swarm; o ficheiro reflete a entrega esperada na submissão.)*

## Desenvolvimento local (sem Docker)

1. **Obter o ficheiro de referência** `references.json.gz` a partir do repositório do desafio (pasta `resources/`), ou `example-references.json` só para testes rápidos.

2. **Gerar** `references.bin`:

   ```bash
   dotnet run --project tools/RinhaRefConverter -- caminho/para/references.json.gz caminho/para/references.bin
   ```

3. **Colocar o binário** onde a API procura por defeito, ou apontar com variável de ambiente:
   - Caminho padrão: `src/RinhaFraudApi/bin/Debug/net8.0/data/references.bin` (pasta `data` ao lado do output do build), **ou**
   - `REFERENCES_PATH` = caminho absoluto para o ficheiro `.bin`.

4. **Compilar e executar** a API:

   ```bash
   dotnet build RinhaFraud.slnx
   dotnet run --project src/RinhaFraudApi
   ```

   Por defeito o perfil de lançamento usa `http://127.0.0.1:9999`.

5. Sem `references.bin` válido, **`GET /ready`** responde **503** (serviço não pronto).

## Arquitetura de balanceamento

```
Cliente → Nginx (:9999) → round-robin → API 1 (:8080) | API 2 (:8080)
```

O Nginx **não** aplica regra de negócio; apenas reparte pedidos entre as duas instâncias, conforme [ARQUITETURA.md](https://github.com/zanfranceschi/rinha-de-backend-2026/blob/main/docs/br/ARQUITETURA.md).

## Submissão na Rinha

O fluxo oficial (branch `submission`, `docker-compose` na raiz, issue com `rinha/test`, etc.) está descrito em [SUBMISSAO.md](https://github.com/zanfranceschi/rinha-de-backend-2026/blob/main/docs/br/SUBMISSAO.md).

## Referências

- [Repositório do desafio 2026](https://github.com/zanfranceschi/rinha-de-backend-2026)  
- [Avaliação (pontuação)](https://github.com/zanfranceschi/rinha-de-backend-2026/blob/main/docs/br/AVALIACAO.md)  
- [Dataset (references, MCC, normalização)](https://github.com/zanfranceschi/rinha-de-backend-2026/blob/main/docs/br/DATASET.md)
