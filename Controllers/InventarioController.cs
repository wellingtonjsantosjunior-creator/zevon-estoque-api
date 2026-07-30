using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using Dapper;

namespace ZevonEstoque.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class InventarioController : ControllerBase
{
    private readonly string _connectionString;

    public InventarioController(string connectionString)
    {
        _connectionString = connectionString;
    }

    // ── VERIFICAR SE FILIAL ESTÁ EM INVENTÁRIO ───────────────────
    [HttpGet("status/{idFilial}")]
    public async Task<IActionResult> VerificarStatus(int idFilial)
    {
        using var conn = new NpgsqlConnection(_connectionString);

        var inventario = await conn.QueryFirstOrDefaultAsync(@"
            SELECT id_inventario AS idInventario,
                   status,
                   data_programada AS dataProgramada,
                   hora_inicio AS horaInicio,
                   hora_fim AS horaFim
            FROM Inventarios
            WHERE id_filial = @IdFilial
              AND status IN ('PROGRAMADO', 'EM_ANDAMENTO')
              AND data_programada = CAST(GETDATE() AS DATE)
            ORDER BY criado_em DESC",
            new { IdFilial = idFilial });

        return Ok(new
        {
            emInventario = inventario != null &&
                           (string)inventario.status == "EM_ANDAMENTO",
            programado = inventario != null,
            inventario
        });
    }

    // ── LISTAR INVENTÁRIOS ────────────────────────────────────────
    [HttpGet]
    public async Task<IActionResult> Listar([FromQuery] int? idFilial)
    {
        using var conn =new NpgsqlConnection(_connectionString);
        var inventarios = await conn.QueryAsync(@"
            SELECT
                i.id_inventario AS idInventario,
                i.id_filial AS idFilial,
                f.nome AS filial,
                i.data_programada AS dataProgramada,
                i.hora_inicio AS horaInicio,
                i.hora_fim AS horaFim,
                i.status,
                i.criado_em AS criadoEm,
                i.aprovado_em AS aprovadoEm,
                i.observacao_aprovador AS observacaoAprovador,
                uc.nome AS nomeAprovador,
                -- contagens
                (SELECT COUNT(*) FROM InventarioFichas f2
                 WHERE f2.id_inventario = i.id_inventario) AS totalFichas,
                (SELECT COUNT(*) FROM InventarioFichas f2
                 WHERE f2.id_inventario = i.id_inventario
                   AND f2.saldo_contado IS NOT NULL) AS fichasContadas,
                (SELECT COUNT(*) FROM InventarioFichas f2
                 WHERE f2.id_inventario = i.id_inventario
                   AND f2.divergencia != 0
                   AND f2.saldo_contado IS NOT NULL) AS fichasComDivergencia
            FROM Inventarios i
            INNER JOIN Filiais f ON i.id_filial = f.id_filial
            LEFT JOIN Usuarios uc ON i.id_usuario_aprovador = uc.id_usuario
            WHERE (@IdFilial IS NULL OR i.id_filial = @IdFilial)
            ORDER BY i.data_programada DESC, i.criado_em DESC",
            new { IdFilial = idFilial });

        return Ok(inventarios);
    }

    // ── CRIAR INVENTÁRIO ─────────────────────────────────────────
    [HttpPost]
    public async Task<IActionResult> Criar([FromBody] CriarInventarioRequest request)
    {
        using var conn = new NpgsqlConnection(_connectionString);

        // Verifica se já existe inventário programado/em andamento
        var jaExiste = await conn.QueryFirstOrDefaultAsync(@"
            SELECT id_inventario FROM Inventarios
            WHERE id_filial = @IdFilial
              AND status IN ('PROGRAMADO', 'EM_ANDAMENTO')
              AND data_programada = @DataProgramada",
            new { request.IdFilial, request.DataProgramada });

        if (jaExiste != null)
            return BadRequest("Já existe um inventário programado para essa data.");

        var id = await conn.QueryFirstAsync<int>(@"
            INSERT INTO Inventarios
            (id_filial, data_programada, hora_inicio, hora_fim,
             status, id_usuario_criador)
            VALUES
            (@IdFilial, @DataProgramada, @HoraInicio, @HoraFim,
             'PROGRAMADO', @IdUsuarioCriador);
            SELECT SCOPE_IDENTITY();",
            new
            {
                request.IdFilial,
                request.DataProgramada,
                request.HoraInicio,
                request.HoraFim,
                request.IdUsuarioCriador
            });

        return Ok(new { idInventario = id, mensagem = "Inventário programado com sucesso." });
    }

    // ── INICIAR INVENTÁRIO ────────────────────────────────────────
    [HttpPut("{id}/iniciar")]
    public async Task<IActionResult> Iniciar(int id)
    {
        using var conn = new NpgsqlConnection(_connectionString);

        var inventario = await conn.QueryFirstOrDefaultAsync(@"
            SELECT * FROM Inventarios WHERE id_inventario = @Id",
            new { Id = id });

        if (inventario == null) return NotFound();

        // Gera fichas para todas as prateleiras da filial
        await conn.ExecuteAsync(@"
            UPDATE Inventarios SET status = 'EM_ANDAMENTO'
            WHERE id_inventario = @Id",
            new { Id = id });

        // Cria fichas para cada produto em cada prateleira
        await conn.ExecuteAsync(@"
            INSERT INTO InventarioFichas
            (id_inventario, id_prateleira, id_produto, saldo_sistema)
            SELECT @IdInventario, pp.id_prateleira, pp.id_produto,
                   ISNULL(ef.qtd_atual, 0)
            FROM ProdutoPrateleira pp
            INNER JOIN Prateleiras pr ON pr.id_prateleira = pp.id_prateleira
            LEFT JOIN EstoqueFilial ef ON ef.id_produto = pp.id_produto
                AND ef.id_filial = pr.id_filial
            WHERE pr.id_filial = @IdFilial
              AND pr.ativo = true
            -- evita duplicata
            AND NOT EXISTS (
                SELECT 1 FROM InventarioFichas f2
                WHERE f2.id_inventario = @IdInventario
                  AND f2.id_prateleira = pp.id_prateleira
                  AND f2.id_produto = pp.id_produto
            )",
            new { IdInventario = id, IdFilial = (int)inventario.id_filial });

        return Ok("Inventário iniciado.");
    }

    // ── BUSCAR FICHAS POR PRATELEIRA ─────────────────────────────
    [HttpGet("{id}/prateleira/{codigoBarras}")]
    public async Task<IActionResult> BuscarFichasPrateleira(
        int id, string codigoBarras)
    {
        using var conn =new NpgsqlConnection(_connectionString);

        var prateleira = await conn.QueryFirstOrDefaultAsync(@"
            SELECT id_prateleira AS idPrateleira,
                   descricao, codigo_barras AS codigoBarras
            FROM Prateleiras
            WHERE codigo_barras = @CodigoBarras",
            new { CodigoBarras = codigoBarras });

        if (prateleira == null)
            return NotFound("Prateleira não encontrada.");

        var fichas = await conn.QueryAsync(@"
            SELECT
                f.id_ficha AS idFicha,
                f.id_produto AS idProduto,
                p.nome AS produto,
                p.codigo_sku AS sku,
                p.unidade,
                f.saldo_sistema AS saldoSistema,
                f.saldo_contado AS saldoContado,
                f.divergencia,
                (SELECT TOP 1 codigo_barras FROM Etiquetas
                 WHERE id_produto = f.id_produto ORDER BY id_etiqueta DESC) AS codigoBarras
            FROM InventarioFichas f
            INNER JOIN Produtos p ON f.id_produto = p.id_produto
            WHERE f.id_inventario = @IdInventario
              AND f.id_prateleira = @IdPrateleira
            ORDER BY p.nome",
            new
            {
                IdInventario = id,
                IdPrateleira = (int)prateleira.idPrateleira
            });

        return Ok(new
        {
            prateleira,
            fichas
        });
    }

    // ── SALVAR CONTAGEM ───────────────────────────────────────────
    [HttpPut("ficha/{idFicha}")]
    public async Task<IActionResult> SalvarContagem(
        int idFicha, [FromBody] SalvarContagemRequest request)
    {
        using var conn =new NpgsqlConnection(_connectionString);

        var ficha = await conn.QueryFirstOrDefaultAsync(@"
            SELECT saldo_sistema FROM InventarioFichas
            WHERE id_ficha = @IdFicha",
            new { IdFicha = idFicha });

        if (ficha == null) return NotFound();

        int divergencia = request.SaldoContado - (int)ficha.saldo_sistema;

        await conn.ExecuteAsync(@"
            UPDATE InventarioFichas
            SET saldo_contado = @SaldoContado,
                divergencia = @Divergencia
            WHERE id_ficha = @IdFicha",
            new
            {
                request.SaldoContado,
                Divergencia = divergencia,
                IdFicha = idFicha
            });

        return Ok(new { divergencia });
    }

    // ── FINALIZAR E ENVIAR PARA APROVAÇÃO ─────────────────────────
    [HttpPut("{id}/finalizar")]
    public async Task<IActionResult> Finalizar(
        int id, [FromBody] FinalizarInventarioRequest request)
    {
        using var conn =new NpgsqlConnection(_connectionString);

        await conn.ExecuteAsync(@"
            UPDATE Inventarios
            SET status = 'AGUARDANDO_APROVACAO',
                id_usuario_aprovador = @IdAprovador
            WHERE id_inventario = @Id",
            new { IdAprovador = request.IdUsuarioAprovador, Id = id });

        // Notifica aprovador
        await conn.ExecuteAsync(@"
            INSERT INTO Notificacoes (id_usuario, titulo, corpo)
            VALUES (@IdUsuario, @Titulo, @Corpo)",
            new
            {
                IdUsuario = request.IdUsuarioAprovador,
                Titulo = "Inventário aguardando aprovação",
                Corpo = $"O inventário #{id} foi finalizado e aguarda sua aprovação."
            });

        return Ok("Inventário enviado para aprovação.");
    }

    // ── APROVAR INVENTÁRIO ────────────────────────────────────────
    [HttpPut("{id}/aprovar")]
    public async Task<IActionResult> Aprovar(
        int id, [FromBody] AprovarInventarioRequest request)
    {
        using var conn =new NpgsqlConnection(_connectionString);

        var inventario = await conn.QueryFirstOrDefaultAsync(@"
            SELECT * FROM Inventarios WHERE id_inventario = @Id",
            new { Id = id });

        if (inventario == null) return NotFound();

        // Salva assinatura e aprova
        await conn.ExecuteAsync(@"
            UPDATE Inventarios
            SET status = 'APROVADO',
                assinatura_base64 = @Assinatura,
                observacao_aprovador = @Observacao,
                aprovado_em = GETDATE()
            WHERE id_inventario = @Id",
            new
            {
                Assinatura = request.AssinaturaBase64,
                Observacao = request.Observacao,
                Id = id
            });

       // Busca todas as fichas contadas (não só divergentes)
var fichasContadas = await conn.QueryAsync(@"
    SELECT f.id_produto, f.saldo_contado, pr.id_filial,
           f.id_prateleira, f.id_ficha, f.divergencia
    FROM InventarioFichas f
    INNER JOIN Prateleiras pr ON pr.id_prateleira = f.id_prateleira
    WHERE f.id_inventario = @Id
      AND f.saldo_contado IS NOT NULL",
    new { Id = id });

foreach (var ficha in fichasContadas)
{
    // Atualiza saldo no EstoqueFilial
    await conn.ExecuteAsync(@"
        UPDATE EstoqueFilial
        SET qtd_atual = @SaldoContado
        WHERE id_produto = @IdProduto
          AND id_filial = @IdFilial",
        new
        {
            SaldoContado = (int)ficha.saldo_contado,
            IdProduto = (int)ficha.id_produto,
            IdFilial = (int)ficha.id_filial
        });

    // Registra no Kardex como INVENTARIO
    var saldoApos = (int)ficha.saldo_contado;
    var tipo = (int)ficha.divergencia > 0 ? "ENTRADA" : "SAIDA";
    var qtd = Math.Abs((int)ficha.divergencia);

    await conn.ExecuteAsync(@"
        INSERT INTO Movimentacoes
        (id_produto, id_filial, id_prateleira, id_usuario,
         tipo, quantidade, saldo_apos, observacao, origem_scan)
        VALUES
        (@IdProduto, @IdFilial, @IdPrateleira, @IdUsuario,
         @Tipo, @Quantidade, @SaldoApos,
         'REPROCESSADO POR INVENTÁRIO #' + CAST(@IdInventario AS NVARCHAR),
         0)",
        new
        {
            IdProduto = (int)ficha.id_produto,
            IdFilial = (int)ficha.id_filial,
            IdPrateleira = (int)ficha.id_prateleira,
            IdUsuario = request.IdUsuarioAprovador,
            Tipo = tipo,
            Quantidade = qtd,
            SaldoApos = saldoApos,
            IdInventario = id
        });

    // Marca ficha como reprocessada
    await conn.ExecuteAsync(@"
        UPDATE InventarioFichas SET reprocessado = 1
        WHERE id_ficha = @IdFicha",
        new { IdFicha = (int)ficha.id_ficha });
}

// Fecha inventário
await conn.ExecuteAsync(@"
    UPDATE Inventarios SET status = 'APROVADO'
    WHERE id_inventario = @Id",
    new { Id = id });

return Ok(new
{
    mensagem = "Inventário aprovado e saldos reprocessados.",
    totalReprocessados = fichasContadas.Count()
});


    // ── REJEITAR ──────────────────────────────────────────────────
    [HttpPut("{id}/rejeitar")]
     async Task<IActionResult> Rejeitar(
        int id, [FromBody] AprovarInventarioRequest request)
    {
        using var conn = new NpgsqlConnection(_connectionString);

        await conn.ExecuteAsync(@"
            UPDATE Inventarios
            SET status = 'REJEITADO',
                observacao_aprovador = @Observacao,
                aprovado_em = GETDATE()
            WHERE id_inventario = @Id",
            new { Observacao = request.Observacao, Id = id });

        // Volta para EM_ANDAMENTO para corrigir
        await conn.ExecuteAsync(@"
            UPDATE Inventarios SET status = 'EM_ANDAMENTO'
            WHERE id_inventario = @Id",
            new { Id = id });

        return Ok("Inventário rejeitado. Retornado para correção.");
    }

    // ── BUSCAR INVENTÁRIO COMPLETO (BLOCO) ────────────────────────
    [HttpGet("{id}/bloco")]
     async Task<IActionResult> BuscarBloco(int id)
    {
        using var conn = new NpgsqlConnection(_connectionString);

        var inventario = await conn.QueryFirstOrDefaultAsync(@"
            SELECT
                i.id_inventario AS idInventario,
                i.id_filial AS idFilial,
                f.nome AS filial,
                i.data_programada AS dataProgramada,
                i.status,
                i.criado_em AS criadoEm,
                i.aprovado_em AS aprovadoEm,
                i.assinatura_base64 AS assinaturaBase64,
                i.observacao_aprovador AS observacaoAprovador,
                ua.nome AS nomeAprovador
            FROM Inventarios i
            INNER JOIN Filiais f ON i.id_filial = f.id_filial
            LEFT JOIN Usuarios ua ON i.id_usuario_aprovador = ua.id_usuario
            WHERE i.id_inventario = @Id",
            new { Id = id });

        if (inventario == null) return NotFound();

        // Fichas agrupadas por prateleira
        var fichas = await conn.QueryAsync(@"
            SELECT
                pr.codigo_barras AS codigoPrateleira,
                pr.descricao AS descricaoPrateleira,
                f.id_ficha AS idFicha,
                p.nome AS produto,
                p.codigo_sku AS sku,
                p.unidade,
                f.saldo_sistema AS saldoSistema,
                f.saldo_contado AS saldoContado,
                f.divergencia,
                f.reprocessado
            FROM InventarioFichas f
            INNER JOIN Prateleiras pr ON f.id_prateleira = pr.id_prateleira
            INNER JOIN Produtos p ON f.id_produto = p.id_produto
            WHERE f.id_inventario = @Id
            ORDER BY pr.codigo_barras, p.nome",
            new { Id = id });

        // Agrupa por prateleira
        var fichasPorPrateleira = fichas
            .GroupBy(f => new
            {
                codigo = (string)f.codigoPrateleira,
                descricao = (string)f.descricaoPrateleira
            })
            .Select(g => new
            {
                codigoPrateleira = g.Key.codigo,
                descricaoPrateleira = g.Key.descricao,
                itens = g.ToList()
            })
            .ToList();

        return Ok(new { inventario, fichasPorPrateleira });
    }
}

// ── MODELS ────────────────────────────────────────────────────
public class CriarInventarioRequest
{
    public int IdFilial { get; set; }
    public DateTime DataProgramada { get; set; }
    public string HoraInicio { get; set; } = string.Empty;
    public string HoraFim { get; set; } = string.Empty;
    public int IdUsuarioCriador { get; set; }
}

public class SalvarContagemRequest
{
    public int SaldoContado { get; set; }
}

public class FinalizarInventarioRequest
{
    public int IdUsuarioAprovador { get; set; }
}

public class AprovarInventarioRequest
{
    public int IdUsuarioAprovador { get; set; }
    public string? AssinaturaBase64 { get; set; }
    public string? Observacao { get; set; }
}}