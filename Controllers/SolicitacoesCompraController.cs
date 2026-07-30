using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using Dapper;

namespace ZevonEstoque.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class SolicitacoesCompraController : ControllerBase
{
    private readonly string _connectionString;

    public SolicitacoesCompraController(string connectionString)
    {
        _connectionString = connectionString;
    }

    // ── LISTAR ────────────────────────────────────────────────────
    [HttpGet]
public async Task<IActionResult> Listar(
    [FromQuery] int? idFilial,
    [FromQuery] string? status)
{
    try
    {
        using var conn = new NpgsqlConnection(_connectionString);
        var result = await conn.QueryAsync(@"
            SELECT
                sc.id_solicitacao AS ""idSolicitacao"",
                sc.id_filial AS ""idFilial"",
                f.nome AS filial,
                sc.id_produto AS ""idProduto"",
                p.nome AS produto,
                p.codigo_sku AS sku,
                p.unidade,
                ef.qtd_atual AS ""saldoAtual"",
                ef.qtd_minima AS ""saldoMinimo"",
                sc.id_usuario_solicitante AS ""idUsuarioSolicitante"",
                us.nome AS ""nomeSolicitante"",
                sc.id_usuario_aprovador AS ""idUsuarioAprovador"",
                ua.nome AS ""nomeAprovador"",
                sc.id_fornecedor AS ""idFornecedor"",
                fo.nome AS ""nomeFornecedor"",
                sc.quantidade,
                sc.quantidade_sugerida AS ""quantidadeSugerida"",
                sc.urgencia,
                sc.status,
                sc.origem,
                sc.observacao,
                sc.observacao_aprovador AS ""observacaoAprovador"",
                sc.numero_protheus AS ""numeroProtheus"",
                sc.criado_em AS ""criadoEm"",
                sc.aprovado_em AS ""aprovadoEm"",
                sc.concluido_em AS ""concluidoEm""
            FROM SolicitacoesCompra sc
            INNER JOIN Filiais f ON sc.id_filial = f.id_filial
            INNER JOIN Produtos p ON sc.id_produto = p.id_produto
            LEFT JOIN EstoqueFilial ef ON ef.id_produto = sc.id_produto
                AND ef.id_filial = sc.id_filial
            LEFT JOIN Usuarios us ON sc.id_usuario_solicitante = us.id_usuario
            LEFT JOIN Usuarios ua ON sc.id_usuario_aprovador = ua.id_usuario
            LEFT JOIN Fornecedores fo ON sc.id_fornecedor = fo.id_fornecedor
            WHERE (@IdFilial IS NULL OR sc.id_filial = @IdFilial)
              AND (@Status IS NULL OR sc.status = @Status)
            ORDER BY
                CASE sc.urgencia
                    WHEN 'CRITICO' THEN 1
                    WHEN 'URGENTE' THEN 2
                    ELSE 3
                END,
                sc.criado_em DESC",
            new { IdFilial = idFilial, Status = status });

        return Ok(result);
    }
    catch (Exception ex)
    {
        return StatusCode(500, new { erro = ex.Message, detalhes = ex.InnerException?.Message });
    }
}

    // ── GERAR SUGESTÕES AUTOMÁTICAS ───────────────────────────────
    [HttpPost("sugestoes/{idFilial}")]
    public async Task<IActionResult> GerarSugestoes(int idFilial)
    {
        using var conn = new NpgsqlConnection(_connectionString);

        // Busca produtos abaixo do mínimo que não têm sugestão ativa
        var produtosAbaixo = await conn.QueryAsync(@"
            SELECT
                ef.id_produto AS ""idProduto"",
                p.nome AS produto,
                ef.qtd_atual AS ""saldoAtual"",
                ef.qtd_minima AS ""saldoMinimo"",
                ef.qtd_minima - ef.qtd_atual AS quantidade
            FROM EstoqueFilial ef
            INNER JOIN Produtos p ON ef.id_produto = p.id_produto
            WHERE ef.id_filial = @IdFilial
              AND ef.qtd_atual <= ef.qtd_minima
              AND p.ativo = true
              AND NOT EXISTS (
                SELECT 1 FROM SolicitacoesCompra sc
                WHERE sc.id_produto = ef.id_produto
                  AND sc.id_filial = @IdFilial
                  AND sc.status IN ('SUGESTAO', 'PENDENTE', 'APROVADO')
              )",
            new { IdFilial = idFilial });

        int criadas = 0;
        foreach (var produto in produtosAbaixo)
        {
            var qtd = (int)produto.quantidade;
            if (qtd <= 0) qtd = (int)produto.saldoMinimo;

            await conn.ExecuteAsync(@"
                INSERT INTO SolicitacoesCompra
                (id_filial, id_produto, quantidade, quantidade_sugerida,
                 urgencia, status, origem)
                VALUES
                (@IdFilial, @IdProduto, @Quantidade, @Quantidade,
                 @Urgencia, 'SUGESTAO', 'AUTO')",
                new
                {
                    IdFilial = idFilial,
                    IdProduto = (int)produto.idProduto,
                    Quantidade = qtd,
                    Urgencia = (int)produto.saldoAtual <= 0 ? "CRITICO" :
                               (int)produto.saldoAtual <= (int)produto.saldoMinimo / 2 ? "URGENTE" : "NORMAL"
                });
            criadas++;
        }

        return Ok(new
        {
            sugestoesCriadas = criadas,
            mensagem = criadas > 0
                ? $"{criadas} sugestão(ões) gerada(s)."
                : "Nenhum produto abaixo do mínimo."
        });
    }

    // ── CRIAR MANUAL ──────────────────────────────────────────────
    [HttpPost]
    public async Task<IActionResult> Criar(
        [FromBody] CriarSolicitacaoRequest request)
    {
        using var conn = new NpgsqlConnection(_connectionString);

        var id = await conn.QueryFirstAsync<int>(@"
            INSERT INTO SolicitacoesCompra
            (id_filial, id_produto, id_usuario_solicitante, id_fornecedor,
             quantidade, quantidade_sugerida, urgencia, status, origem, observacao)
            VALUES
            (@IdFilial, @IdProduto, @IdUsuarioSolicitante, @IdFornecedor,
             @Quantidade, @Quantidade, @Urgencia, 'PENDENTE', 'MANUAL', @Observacao)
            RETURNING id_solicitacao;",
            new
            {
                request.IdFilial,
                request.IdProduto,
                request.IdUsuarioSolicitante,
                request.IdFornecedor,
                request.Quantidade,
                request.Urgencia,
                request.Observacao
            });

        // Notifica aprovadores
        await _notificarAprovadores(conn, request.IdFilial, id);

        return Ok(new { idSolicitacao = id });
    }

    // ── ACEITAR SUGESTÃO E ENVIAR PARA APROVAÇÃO ──────────────────
    [HttpPut("{id}/aceitar")]
    public async Task<IActionResult> Aceitar(
        int id, [FromBody] AceitarSugestaoRequest request)
    {
        using var conn = new NpgsqlConnection(_connectionString);

        await conn.ExecuteAsync(@"
            UPDATE SolicitacoesCompra
            SET status = 'PENDENTE',
                id_usuario_solicitante = @IdUsuario,
                id_usuario_aprovador = @IdAprovador,
                id_fornecedor = @IdFornecedor,
                quantidade = @Quantidade,
                urgencia = @Urgencia,
                observacao = @Observacao
            WHERE id_solicitacao = @Id
              AND status = 'SUGESTAO'",
            new
            {
                request.IdUsuario,
                request.IdAprovador,
                request.IdFornecedor,
                request.Quantidade,
                request.Urgencia,
                request.Observacao,
                Id = id
            });

        // Notifica aprovador selecionado
        await conn.ExecuteAsync(@"
            INSERT INTO Notificacoes (id_usuario, titulo, corpo)
            VALUES (@IdUsuario, @Titulo, @Corpo)",
            new
            {
                IdUsuario = request.IdAprovador,
                Titulo = "Solicitação de Compra para Aprovar",
                Corpo = $"Solicitação de compra #{id} aguarda sua aprovação."
            });

        return Ok("Sugestão aceita e enviada para aprovação.");
    }

    // ── APROVAR ───────────────────────────────────────────────────
    [HttpPut("{id}/aprovar")]
    public async Task<IActionResult> Aprovar(
        int id, [FromBody] AprovarSolicitacaoRequest request)
    {
        using var conn = new NpgsqlConnection(_connectionString);

        var sol = await conn.QueryFirstOrDefaultAsync(@"
            SELECT sc.*, u.id_usuario AS ""idSolicitante""
            FROM SolicitacoesCompra sc
            LEFT JOIN Usuarios u ON sc.id_usuario_solicitante = u.id_usuario
            WHERE sc.id_solicitacao = @Id",
            new { Id = id });

        if (sol == null) return NotFound();

        await conn.ExecuteAsync(@"
            UPDATE SolicitacoesCompra
            SET status = 'APROVADO',
                id_usuario_aprovador = @IdAprovador,
                observacao_aprovador = @Observacao,
                aprovado_em = NOW()
            WHERE id_solicitacao = @Id",
            new
            {
                request.IdAprovador,
                request.Observacao,
                Id = id
            });

        // Notifica solicitante
        if (sol.idSolicitante != null)
        {
            await conn.ExecuteAsync(@"
                INSERT INTO Notificacoes (id_usuario, titulo, corpo)
                VALUES (@IdUsuario, @Titulo, @Corpo)",
                new
                {
                    IdUsuario = (int)sol.idSolicitante,
                    Titulo = "Solicitação de Compra Aprovada",
                    Corpo = $"Solicitação #{id} foi aprovada! Aguardando lançamento no Protheus."
                });
        }

        return Ok("Solicitação aprovada.");
    }

    // ── REJEITAR ──────────────────────────────────────────────────
    [HttpPut("{id}/rejeitar")]
    public async Task<IActionResult> Rejeitar(
        int id, [FromBody] AprovarSolicitacaoRequest request)
    {
        using var conn = new NpgsqlConnection(_connectionString);

        var sol = await conn.QueryFirstOrDefaultAsync(
            "SELECT id_usuario_solicitante FROM SolicitacoesCompra WHERE id_solicitacao = @Id",
            new { Id = id });

        if (sol == null) return NotFound();

        await conn.ExecuteAsync(@"
            UPDATE SolicitacoesCompra
            SET status = 'REJEITADO',
                observacao_aprovador = @Observacao,
                aprovado_em = NOW()
            WHERE id_solicitacao = @Id",
            new { request.Observacao, Id = id });

        if (sol.id_usuario_solicitante != null)
        {
            await conn.ExecuteAsync(@"
                INSERT INTO Notificacoes (id_usuario, titulo, corpo)
                VALUES (@IdUsuario, @Titulo, @Corpo)",
                new
                {
                    IdUsuario = (int)sol.id_usuario_solicitante,
                    Titulo = "Solicitação de Compra Rejeitada",
                    Corpo = $"Solicitação #{id} foi rejeitada. Motivo: {request.Observacao}"
                });
        }

        return Ok("Solicitação rejeitada.");
    }

    // ── CONCLUIR (informar número Protheus) ───────────────────────
    [HttpPut("{id}/concluir")]
    public async Task<IActionResult> Concluir(
        int id, [FromBody] ConcluirSolicitacaoRequest request)
    {
        using var conn = new NpgsqlConnection(_connectionString);

        await conn.ExecuteAsync(@"
            UPDATE SolicitacoesCompra
            SET status = 'CONCLUIDO',
                numero_protheus = @NumeroProtheus,
                concluido_em = NOW()
            WHERE id_solicitacao = @Id
              AND status = 'APROVADO'",
            new { request.NumeroProtheus, Id = id });

        return Ok("Solicitação concluída com número Protheus registrado.");
    }

    // ── DESCARTAR SUGESTÃO ────────────────────────────────────────
    [HttpDelete("{id}")]
    public async Task<IActionResult> Descartar(int id)
    {
        using var conn = new NpgsqlConnection(_connectionString);

        await conn.ExecuteAsync(@"
            DELETE FROM SolicitacoesCompra
            WHERE id_solicitacao = @Id
              AND status = 'SUGESTAO'",
            new { Id = id });

        return Ok("Sugestão descartada.");
    }

    // ── HELPER ────────────────────────────────────────────────────
    private async Task _notificarAprovadores(
        NpgsqlConnection conn, int idFilial, int idSolicitacao)
    {
        var aprovadores = await conn.QueryAsync<dynamic>(@"
            SELECT id_usuario AS ""idUsuario"" FROM Usuarios
            WHERE id_filial = @IdFilial
              AND perfil IN ('ADMIN')
              AND ativo = true",
            new { IdFilial = idFilial });

        foreach (var ap in aprovadores)
        {
            await conn.ExecuteAsync(@"
                INSERT INTO Notificacoes (id_usuario, titulo, corpo)
                VALUES (@IdUsuario, @Titulo, @Corpo)",
                new
                {
                    IdUsuario = (int)ap.idUsuario,
                    Titulo = "Nova Solicitação de Compra",
                    Corpo = $"Solicitação #{idSolicitacao} aguarda aprovação."
                });
        }
    }
}

// ── MODELS ────────────────────────────────────────────────────
public class CriarSolicitacaoRequest
{
    public int IdFilial { get; set; }
    public int IdProduto { get; set; }
    public int IdUsuarioSolicitante { get; set; }
    public int? IdFornecedor { get; set; }
    public int Quantidade { get; set; }
    public string Urgencia { get; set; } = "NORMAL";
    public string? Observacao { get; set; }
}

public class AceitarSugestaoRequest
{
    public int IdUsuario { get; set; }
    public int IdAprovador { get; set; }
    public int? IdFornecedor { get; set; }
    public int Quantidade { get; set; }
    public string Urgencia { get; set; } = "NORMAL";
    public string? Observacao { get; set; }
}

public class AprovarSolicitacaoRequest
{
    public int IdAprovador { get; set; }
    public string? Observacao { get; set; }
}

public class ConcluirSolicitacaoRequest
{
    public string NumeroProtheus { get; set; } = string.Empty;
}