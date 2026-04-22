    using Microsoft.EntityFrameworkCore;
    using NoPonto.Application.DTOs.Itinerarios;
    using NoPonto.Data.Interfaces;

    namespace NoPonto.Data.Repositories;

    public sealed class ItinerarioRepository : IItinerarioRepository
    {
        private readonly TransporteDbContext _contexto;

        public ItinerarioRepository(TransporteDbContext contexto)
        {
            _contexto = contexto;
        }

        public async Task<IReadOnlyList<ItinerarioPorLinhaConsultaDTO>> ListarPorLinhaAsync(Guid linhaId, CancellationToken cancellationToken)
        {
            var itens = await _contexto.Itinerarios
                .AsNoTracking()
                .Where(itinerario => itinerario.Sentido.LinhaId == linhaId)
                .OrderBy(itinerario => itinerario.SentidoId)
                .Select(itinerario => new ItinerarioPorLinhaConsultaDTO
                {
                    Id = itinerario.Id,
                    LinhaId = itinerario.Sentido.LinhaId,
                    SentidoId = itinerario.SentidoId
                })
                .ToListAsync(cancellationToken);

            return itens;
        }

        public async Task<ItinerarioMapaDTO?> BuscarMapaAsync(Guid itinerarioId, CancellationToken cancellationToken)
        {
            var cabecalho = await _contexto.Itinerarios
                .AsNoTracking()
                .Where(itinerario => itinerario.Id == itinerarioId)
                .Select(itinerario => new
                {
                    ItinerarioId = itinerario.Id,
                    LinhaNome = itinerario.Sentido.Linha.Nome,
                    SentidoNome = itinerario.Sentido.Nome,
                    Geometria = itinerario.Geometria
                })
                .FirstOrDefaultAsync(cancellationToken);

            if (cabecalho is null)
                return null;

            var paradas = await _contexto.ParadasItinerario
                .AsNoTracking()
                .Where(relacao => relacao.ItinerarioId == itinerarioId)
                .OrderBy(relacao => relacao.Ordem)
                .Select(relacao => new ItinerarioMapaParadaDTO
                {
                    ParadaId = relacao.ParadaId,
                    Nome = relacao.Parada.Nome,
                    Ordem = relacao.Ordem,
                    Latitude = relacao.Parada.Localizacao.Y,
                    Longitude = relacao.Parada.Localizacao.X,
                    PosicaoLinha = relacao.PosicaoLinha
                })
                .ToListAsync(cancellationToken);

            var geometria = cabecalho.Geometria.Coordinates
                .Select((coordenada, indice) => new ItinerarioMapaCoordenadaDTO
                {
                    Ordem = indice + 1,
                    Latitude = coordenada.Y,
                    Longitude = coordenada.X
                })
                .ToList();

            return new ItinerarioMapaDTO
            {
                ItinerarioId = cabecalho.ItinerarioId,
                LinhaNome = cabecalho.LinhaNome,
                SentidoNome = cabecalho.SentidoNome,
                Geometria = geometria,
                Paradas = paradas
            };
        }

        public Task<bool> ExistePorIdAsync(Guid itinerarioId, CancellationToken cancellationToken)
        {
            return _contexto.Itinerarios
                .AsNoTracking()
                .AnyAsync(itinerario => itinerario.Id == itinerarioId, cancellationToken);
        }
    }