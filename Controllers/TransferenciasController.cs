using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using Dapper;
using ZevonEstoque.Models;

namespace ZevonEstoque.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class TransferenciasController : ControllerBase
{
    private readonly string _connectionString;

    public TransferenciasController(string connectionString)
    {
        _connectionString = connectionString;
    }

    private async Task<bool> FilialEmInventario(NpgsqlConnection conn, int idFilial)
    {
        var inv = await conn.QueryFirstOrDefaultAsync(@"
            SELECT id_inventario FROM Inventarios
            WHERE id_filial = @IdFilial AND status = 'EM_ANDAMENTO'",
            new { IdFilial = idFilial });
        return inv != null;
    }

    // ── LISTAR AGRUPADO ───────────────────────────────────────────
    [HttpGet]
    public async Task<IActionResult> Listar(
        [FromQuery] int? idFilialOrigem,
        [FromQuery] int? idFilialDestino,
        [FromQuery] string? status)
    {
        using var conn = new NpgsqlConnection(_connectionString);
        var itens = await conn.QueryAsync(@"
            SELECT
                t.id_transferencia AS idTransferencia,
                t.id_grupo AS idGrupo,
                t.id_filial_origem AS idFilialOrigem,
                fo.nome AS filialOrigem,
                t.id_filial_destino AS idFilialDestino,
                fd.nome AS filialDestino,
                t.id_produto AS idProduto,
                p.nome AS produto,
                p.codigo_sku AS sku,
                p.unidade,
                t.id_prateleira_origem AS idPrateleiraOrigem,
                pro.codigo_barras AS codigoPrateleiraOrigem,
                pro.descricao AS descricaoPrateleiraOrigem,
                t.id_prateleira_destino AS idPrateleiraDestino,
                prd.codigo_barras AS codigoPrateleiraDestino,
                t.id_usuario_solicitante AS idUsuarioSolicitante,
                us.nome AS nomeSolicitante,
                t.id_usuario_recebedor AS idUsuarioRecebedor,
                ur.nome AS nomeRecebedor,
                t.quantidade,
                t.status,
                t.observacao,
                t.criado_em AS criadoEm,
                t.recebido_em AS recebidoEm
            FROM Transferencias t
            INNER JOIN Filiais fo ON t.id_filial_origem = fo.id_filial
            INNER JOIN Filiais fd ON t.id_filial_destino = fd.id_filial
            INNER JOIN Produtos p ON t.id_produto = p.id_produto
            INNER JOIN Prateleiras pro ON t.id_prateleira_origem = pro.id_prateleira
            LEFT JOIN Prateleiras prd ON t.id_prateleira_destino = prd.id_prateleira
            INNER JOIN Usuarios us ON t.id_usuario_solicitante = us.id_usuario
            LEFT JOIN Usuarios ur ON t.id_usuario_recebedor = ur.id_usuario
            WHERE (@IdFilialOrigem IS NULL OR t.id_filial_origem = @IdFilialOrigem)
              AND (@IdFilialDestino IS NULL OR t.id_filial_destino = @IdFilialDestino)
              AND (@Status IS NULL OR t.status = @Status)
            ORDER BY t.criado_em DESC",
            new { IdFilialOrigem = idFilialOrigem, IdFilialDestino = idFilialDestino, Status = status });

        // Agrupa por id_grupo
        var grupos = itens
            .GroupBy(t => t.idGrupo?.ToString() ?? t.idTransferencia.ToString())
            .Select(g =>
            {
                var primeiro = g.First();
                return new
                {
                    idGrupo = primeiro.idGrupo,
                    idTransferencia = primeiro.idTransferencia,
                    idFilialOrigem = primeiro.idFilialOrigem,
                    filialOrigem = primeiro.filialOrigem,
                    idFilialDestino = primeiro.idFilialDestino,
                    filialDestino = primeiro.filialDestino,
                    nomeSolicitante = primeiro.nomeSolicitante,
                    nomeRecebedor = primeiro.nomeRecebedor,
                    status = primeiro.status,
                    observacao = primeiro.observacao,
                    criadoEm = primeiro.criadoEm,
                    recebidoEm = primeiro.recebidoEm,
                    totalItens = g.Count(),
                    itens = g.Select(i => new
                    {
                        idTransferencia = i.idTransferencia,
                        idProduto = i.idProduto,
                        produto = i.produto,
                        sku = i.sku,
                        unidade = i.unidade,
                        quantidade = i.quantidade,
                        codigoPrateleiraOrigem = i.codigoPrateleiraOrigem,
                        descricaoPrateleiraOrigem = i.descricaoPrateleiraOrigem,
                        codigoPrateleiraDestino = i.codigoPrateleiraDestino,
                        status = i.status,
                    }).ToList()
                };
            })
            .ToList();

        return Ok(grupos);
    }

    // ── CRIAR COM GRUPO ───────────────────────────────────────────
    [HttpPost]
    public async Task<IActionResult> Criar([FromBody] CriarTransferenciaRequest request)
    {
        try
        {
            using var conn = new NpgsqlConnection(_connectionString);

            if (await FilialEmInventario(conn, request.IdFilialOrigem))
                return BadRequest("INVENTARIO_EM_ANDAMENTO_ORIGEM");

            if (await FilialEmInventario(conn, request.IdFilialDestino))
                return BadRequest("INVENTARIO_EM_ANDAMENTO_DESTINO");

            var saldo = await conn.QueryFirstOrDefaultAsync<int?>(@"
                SELECT qtd_atual FROM EstoqueFilial
                WHERE id_produto = @IdProduto AND id_filial = @IdFilial",
                new { request.IdProduto, IdFilial = request.IdFilialOrigem });

            if (saldo == null || saldo < request.Quantidade)
                return BadRequest("SALDO_INSUFICIENTE");

            // Usa idGrupo recebido ou gera novo
            Guid idGrupo;
            if (!string.IsNullOrEmpty(request.IdGrupo) &&
                Guid.TryParse(request.IdGrupo, out var guidParsed))
                idGrupo = guidParsed;
            else
                idGrupo = Guid.NewGuid();

            // Registra saída via SP
            await conn.ExecuteAsync(
                "EXEC sp_SaidaPorPrateleira @codigo_prateleira, @id_produto, @id_usuario, @quantidade, @observacao, @id_requisicao",
                new
                {
                    codigo_prateleira = request.CodigoPrateleiraOrigem,
                    id_produto = request.IdProduto,
                    id_usuario = request.IdUsuarioSolicitante,
                    quantidade = request.Quantidade,
                    observacao = $"TRANSFERENCIA para {request.NomeFilialDestino}",
                    id_requisicao = (int?)null
                });

            // Atualiza movimentação como TRANSFERENCIA_SAIDA
            await conn.ExecuteAsync(@"
                UPDATE Movimentacoes
                SET tipo = 'TRANSFERENCIA_SAIDA',
                    observacao = @Obs
                WHERE id_movimentacao = (
                    SELECT TOP 1 id_movimentacao
                    FROM Movimentacoes
                    WHERE id_produto = @IdProduto
                      AND id_filial = @IdFilialOrigem
                      AND tipo = 'SAIDA'
                    ORDER BY data_hora DESC
                )",
                new
                {
                    Obs = $"TRANSFERENCIA_SAIDA → {request.NomeFilialDestino}",
                    request.IdProduto,
                    IdFilialOrigem = request.IdFilialOrigem
                });

            // Cria registro com id_grupo
            var id = await conn.QueryFirstAsync<int>(@"
                INSERT INTO Transferencias
                (id_filial_origem, id_filial_destino, id_produto,
                 id_prateleira_origem, id_usuario_solicitante, quantidade,
                 status, observacao, id_grupo)
                VALUES
                (@IdFilialOrigem, @IdFilialDestino, @IdProduto,
                 @IdPrateleiraOrigem, @IdUsuarioSolicitante, @Quantidade,
                 'AGUARDANDO', @Observacao, @IdGrupo);
                SELECT SCOPE_IDENTITY();",
                new
                {
                    request.IdFilialOrigem,
                    request.IdFilialDestino,
                    request.IdProduto,
                    request.IdPrateleiraOrigem,
                    request.IdUsuarioSolicitante,
                    request.Quantidade,
                    Observacao = request.Observacao,
                    IdGrupo = idGrupo
                });

            // Notifica apenas no primeiro item do grupo
            if (request.PrimeiroDoGrupo)
            {
                var operadores = await conn.QueryAsync<dynamic>(@"
                    SELECT id_usuario AS idUsuario FROM Usuarios
                    WHERE id_filial = @IdFilial
                      AND perfil IN ('ADMIN','OPERADOR')
                      AND ativo = 1",
                    new { IdFilial = request.IdFilialDestino });

                foreach (var op in operadores)
                {
                    await conn.ExecuteAsync(@"
                        INSERT INTO Notificacoes (id_usuario, titulo, corpo)
                        VALUES (@IdUsuario, @Titulo, @Corpo)",
                        new
                        {
                            IdUsuario = (int)op.idUsuario,
                            Titulo = "Nova Transferência Recebida",
                            Corpo = $"Transferência de {request.NomeFilialOrigem} aguardando recebimento."
                        });
                }
            }

            return Ok(new
            {
                idTransferencia = id,
                idGrupo = idGrupo.ToString(),
                mensagem = "Transferência criada com sucesso."
            });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { erro = ex.Message, detalhes = ex.InnerException?.Message });
        }
    }

    // ── BUSCAR GRUPO POR QR CODE ──────────────────────────────────
    [HttpGet("grupo/{idGrupo}")]
    public async Task<IActionResult> BuscarGrupo(string idGrupo)
    {
        using var conn = new NpgsqlConnection(_connectionString);
        var itens = await conn.QueryAsync(@"
            SELECT
                t.id_transferencia AS idTransferencia,
                t.id_grupo AS idGrupo,
                t.id_filial_origem AS idFilialOrigem,
                fo.nome AS filialOrigem,
                t.id_filial_destino AS idFilialDestino,
                fd.nome AS filialDestino,
                t.id_produto AS idProduto,
                p.nome AS produto,
                p.codigo_sku AS sku,
                p.unidade,
                t.id_prateleira_origem AS idPrateleiraOrigem,
                pro.codigo_barras AS codigoPrateleiraOrigem,
                t.id_usuario_solicitante AS idUsuarioSolicitante,
                us.nome AS nomeSolicitante,
                t.quantidade,
                t.status,
                t.criado_em AS criadoEm
            FROM Transferencias t
            INNER JOIN Filiais fo ON t.id_filial_origem = fo.id_filial
            INNER JOIN Filiais fd ON t.id_filial_destino = fd.id_filial
            INNER JOIN Produtos p ON t.id_produto = p.id_produto
            INNER JOIN Prateleiras pro ON t.id_prateleira_origem = pro.id_prateleira
            INNER JOIN Usuarios us ON t.id_usuario_solicitante = us.id_usuario
            WHERE t.id_grupo = @IdGrupo
            ORDER BY t.id_transferencia",
            new { IdGrupo = idGrupo });

        if (!itens.Any()) return NotFound("Grupo não encontrado.");

        var primeiro = itens.First();
        return Ok(new
        {
            idGrupo = idGrupo,
            idFilialOrigem = primeiro.idFilialOrigem,
            filialOrigem = primeiro.filialOrigem,
            idFilialDestino = primeiro.idFilialDestino,
            filialDestino = primeiro.filialDestino,
            nomeSolicitante = primeiro.nomeSolicitante,
            status = primeiro.status,
            criadoEm = primeiro.criadoEm,
            itens = itens.Select(i => new
            {
                idTransferencia = i.idTransferencia,
                idProduto = i.idProduto,
                produto = i.produto,
                sku = i.sku,
                unidade = i.unidade,
                quantidade = i.quantidade,
                codigoPrateleiraOrigem = i.codigoPrateleiraOrigem,
                status = i.status,
            }).ToList()
        });
    }

    // ── BUSCAR POR ID ─────────────────────────────────────────────
    [HttpGet("{id}")]
    public async Task<IActionResult> BuscarPorId(int id)
    {
        using var conn = new NpgsqlConnection(_connectionString);
        var result = await conn.QueryFirstOrDefaultAsync(@"
            SELECT
                t.id_transferencia AS idTransferencia,
                t.id_grupo AS idGrupo,
                t.id_filial_origem AS idFilialOrigem,
                fo.nome AS filialOrigem,
                t.id_filial_destino AS idFilialDestino,
                fd.nome AS filialDestino,
                t.id_produto AS idProduto,
                p.nome AS produto,
                p.codigo_sku AS sku,
                p.unidade,
                t.id_prateleira_origem AS idPrateleiraOrigem,
                pro.codigo_barras AS codigoPrateleiraOrigem,
                t.id_usuario_solicitante AS idUsuarioSolicitante,
                us.nome AS nomeSolicitante,
                t.quantidade,
                t.status,
                t.criado_em AS criadoEm
            FROM Transferencias t
            INNER JOIN Filiais fo ON t.id_filial_origem = fo.id_filial
            INNER JOIN Filiais fd ON t.id_filial_destino = fd.id_filial
            INNER JOIN Produtos p ON t.id_produto = p.id_produto
            INNER JOIN Prateleiras pro ON t.id_prateleira_origem = pro.id_prateleira
            INNER JOIN Usuarios us ON t.id_usuario_solicitante = us.id_usuario
            WHERE t.id_transferencia = @Id",
            new { Id = id });

        if (result == null) return NotFound("Transferência não encontrada.");
        return Ok(result);
    }

    // ── RECEBER GRUPO INTEIRO ─────────────────────────────────────
    [HttpPut("grupo/{idGrupo}/receber")]
    public async Task<IActionResult> ReceberGrupo(
        string idGrupo, [FromBody] ReceberGrupoRequest request)
    {
        using var conn = new NpgsqlConnection(_connectionString);

        var itens = await conn.QueryAsync(@"
            SELECT t.id_transferencia AS idTransferencia,
                   t.id_produto AS idProduto,
                   t.quantidade,
                   t.id_usuario_solicitante AS idUsuarioSolicitante,
                   fo.nome AS filialOrigem
            FROM Transferencias t
            INNER JOIN Filiais fo ON t.id_filial_origem = fo.id_filial
            WHERE t.id_grupo = @IdGrupo AND t.status = 'AGUARDANDO'",
            new { IdGrupo = idGrupo });

        if (!itens.Any())
            return NotFound("Grupo não encontrado ou já recebido.");

        if (await FilialEmInventario(conn, request.IdFilialDestino))
            return BadRequest("INVENTARIO_EM_ANDAMENTO_DESTINO");

        foreach (var item in itens)
        {
            // Busca prateleira destino do item
            var prateleiraDestino = request.Itens
                .FirstOrDefault(i => i.IdTransferencia == (int)item.idTransferencia);

            if (prateleiraDestino == null) continue;

            // Registra entrada via SP
            await conn.ExecuteAsync(
                "EXEC sp_EntradaEstoque @id_produto, @id_filial, @id_prateleira, @id_usuario, @quantidade, @observacao",
                new
                {
                    id_produto = (int)item.idProduto,
                    id_filial = request.IdFilialDestino,
                    id_prateleira = prateleiraDestino.IdPrateleiraDestino,
                    id_usuario = request.IdUsuarioRecebedor,
                    quantidade = (int)item.quantidade,
                    observacao = $"TRANSFERENCIA_ENTRADA de {item.filialOrigem} #{item.idTransferencia}"
                });

            // Atualiza movimentação como TRANSFERENCIA_ENTRADA
            await conn.ExecuteAsync(@"
                UPDATE Movimentacoes
                SET tipo = 'TRANSFERENCIA_ENTRADA',
                    observacao = @Obs
                WHERE id_movimentacao = (
                    SELECT TOP 1 id_movimentacao
                    FROM Movimentacoes
                    WHERE id_produto = @IdProduto
                      AND id_filial = @IdFilial
                      AND tipo = 'ENTRADA'
                    ORDER BY data_hora DESC
                )",
                new
                {
                    Obs = $"TRANSFERENCIA_ENTRADA ← {item.filialOrigem}",
                    IdProduto = (int)item.idProduto,
                    IdFilial = request.IdFilialDestino
                });

            // Atualiza status do item
            await conn.ExecuteAsync(@"
                UPDATE Transferencias
                SET status = 'RECEBIDO',
                    id_prateleira_destino = @IdPrateleiraDestino,
                    id_usuario_recebedor = @IdUsuarioRecebedor,
                    recebido_em = GETDATE()
                WHERE id_transferencia = @IdTransferencia",
                new
                {
                    IdPrateleiraDestino = prateleiraDestino.IdPrateleiraDestino,
                    request.IdUsuarioRecebedor,
                    IdTransferencia = (int)item.idTransferencia
                });
        }

        // Notifica solicitante uma vez
        var solicitante = itens.First();
        await conn.ExecuteAsync(@"
            INSERT INTO Notificacoes (id_usuario, titulo, corpo)
            VALUES (@IdUsuario, @Titulo, @Corpo)",
            new
            {
                IdUsuario = (int)solicitante.idUsuarioSolicitante,
                Titulo = "Transferência Recebida",
                Corpo = $"Sua transferência foi recebida com sucesso!"
            });

        return Ok("Grupo recebido com sucesso.");
    }

    // ── RECEBER ITEM ÚNICO (compatibilidade) ──────────────────────
    [HttpPut("{id}/receber")]
    public async Task<IActionResult> Receber(
        int id, [FromBody] ReceberTransferenciaRequest request)
    {
        using var conn = new NpgsqlConnection(_connectionString);

        var transferencia = await conn.QueryFirstOrDefaultAsync(@"
            SELECT t.*, pr.id_filial AS idFilialOrigem, fo.nome AS filialOrigem
            FROM Transferencias t
            INNER JOIN Prateleiras pr ON t.id_prateleira_origem = pr.id_prateleira
            INNER JOIN Filiais fo ON t.id_filial_origem = fo.id_filial
            WHERE t.id_transferencia = @Id AND t.status = 'AGUARDANDO'",
            new { Id = id });

        if (transferencia == null)
            return NotFound("Transferência não encontrada ou já recebida.");

        if (await FilialEmInventario(conn, request.IdFilialDestino))
            return BadRequest("INVENTARIO_EM_ANDAMENTO_DESTINO");

        await conn.ExecuteAsync(
            "EXEC sp_EntradaEstoque @id_produto, @id_filial, @id_prateleira, @id_usuario, @quantidade, @observacao",
            new
            {
                id_produto = (int)transferencia.id_produto,
                id_filial = request.IdFilialDestino,
                id_prateleira = request.IdPrateleiraDestino,
                id_usuario = request.IdUsuarioRecebedor,
                quantidade = (int)transferencia.quantidade,
                observacao = $"TRANSFERENCIA_ENTRADA de {request.NomeFilialOrigem} #{id}"
            });

        await conn.ExecuteAsync(@"
            UPDATE Movimentacoes
            SET tipo = 'TRANSFERENCIA_ENTRADA',
                observacao = @Obs
            WHERE id_movimentacao = (
                SELECT TOP 1 id_movimentacao
                FROM Movimentacoes
                WHERE id_produto = @IdProduto
                  AND id_filial = @IdFilial
                  AND tipo = 'ENTRADA'
                ORDER BY data_hora DESC
            )",
            new
            {
                Obs = $"TRANSFERENCIA_ENTRADA ← {request.NomeFilialOrigem} #{id}",
                IdProduto = (int)transferencia.id_produto,
                IdFilial = request.IdFilialDestino
            });

        await conn.ExecuteAsync(@"
            UPDATE Transferencias
            SET status = 'RECEBIDO',
                id_prateleira_destino = @IdPrateleiraDestino,
                id_usuario_recebedor = @IdUsuarioRecebedor,
                recebido_em = GETDATE()
            WHERE id_transferencia = @Id",
            new
            {
                request.IdPrateleiraDestino,
                request.IdUsuarioRecebedor,
                Id = id
            });

        await conn.ExecuteAsync(@"
            INSERT INTO Notificacoes (id_usuario, titulo, corpo)
            VALUES (@IdUsuario, @Titulo, @Corpo)",
            new
            {
                IdUsuario = (int)transferencia.id_usuario_solicitante,
                Titulo = "Transferência Recebida",
                Corpo = $"Transferência #{id} foi recebida com sucesso!"
            });

        return Ok("Transferência recebida com sucesso.");
    }
}

// ── MODELS ────────────────────────────────────────────────────
public class CriarTransferenciaRequest
{
    public int IdFilialOrigem { get; set; }
    public int IdFilialDestino { get; set; }
    public int IdProduto { get; set; }
    public int IdPrateleiraOrigem { get; set; }
    public string CodigoPrateleiraOrigem { get; set; } = string.Empty;
    public int IdUsuarioSolicitante { get; set; }
    public int Quantidade { get; set; }
    public string? Observacao { get; set; }
    public string NomeFilialOrigem { get; set; } = string.Empty;
    public string NomeFilialDestino { get; set; } = string.Empty;
    public string? IdGrupo { get; set; }
    public bool PrimeiroDoGrupo { get; set; } = true;
}

public class ReceberTransferenciaRequest
{
    public int IdFilialDestino { get; set; }
    public int IdPrateleiraDestino { get; set; }
    public int IdUsuarioRecebedor { get; set; }
    public string NomeFilialOrigem { get; set; } = string.Empty;
}

public class ReceberGrupoRequest
{
    public int IdFilialDestino { get; set; }
    public int IdUsuarioRecebedor { get; set; }
    public List<ItemReceberRequest> Itens { get; set; } = new();
}

public class ItemReceberRequest
{
    public int IdTransferencia { get; set; }
    public int IdPrateleiraDestino { get; set; }
}