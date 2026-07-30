-- ============================================================
--  ZevonEstoque - Migração SQL Server -> PostgreSQL
--  Completa o schema que ficou faltando na migração e recria
--  as stored procedures como funções PL/pgSQL.
--  Idempotente: pode rodar quantas vezes for necessário.
-- ============================================================

-- ------------------------------------------------------------
-- 1. Colunas que faltaram em REQUISICOES
-- ------------------------------------------------------------
ALTER TABLE requisicoes ADD COLUMN IF NOT EXISTS data_retirada_prevista timestamp;
ALTER TABLE requisicoes ADD COLUMN IF NOT EXISTS valor_unitario numeric(18,2) NOT NULL DEFAULT 0;
ALTER TABLE requisicoes ADD COLUMN IF NOT EXISTS valor_total    numeric(18,2) NOT NULL DEFAULT 0;
ALTER TABLE requisicoes ADD COLUMN IF NOT EXISTS id_grupo       uuid;

-- ------------------------------------------------------------
-- 2. Coluna que faltou em TRANSFERENCIAS
-- ------------------------------------------------------------
ALTER TABLE transferencias ADD COLUMN IF NOT EXISTS id_grupo uuid;

-- ------------------------------------------------------------
-- 3. Tabela SOLICITACOESCOMPRA (não existia)
-- ------------------------------------------------------------
CREATE TABLE IF NOT EXISTS solicitacoescompra (
    id_solicitacao         serial PRIMARY KEY,
    id_filial              integer NOT NULL REFERENCES filiais(id_filial),
    id_produto             integer NOT NULL REFERENCES produtos(id_produto),
    id_usuario_solicitante integer NOT NULL REFERENCES usuarios(id_usuario),
    id_usuario_aprovador   integer REFERENCES usuarios(id_usuario),
    id_fornecedor          integer REFERENCES fornecedores(id_fornecedor),
    quantidade             integer NOT NULL,
    quantidade_sugerida    integer,
    urgencia               varchar(20)  NOT NULL DEFAULT 'NORMAL',
    status                 varchar(20)  NOT NULL DEFAULT 'PENDENTE',
    origem                 varchar(20)  NOT NULL DEFAULT 'MANUAL',
    observacao             varchar(500),
    observacao_aprovador   varchar(500),
    numero_protheus        varchar(50),
    criado_em              timestamp NOT NULL DEFAULT NOW(),
    aprovado_em            timestamp,
    concluido_em           timestamp
);

CREATE INDEX IF NOT EXISTS ix_solicitacoescompra_filial_status
    ON solicitacoescompra (id_filial, status);

-- ------------------------------------------------------------
-- 4. Índices/unicidade usados pelas rotinas de estoque
-- ------------------------------------------------------------
CREATE UNIQUE INDEX IF NOT EXISTS ux_estoquefilial_produto_filial
    ON estoquefilial (id_produto, id_filial);

CREATE INDEX IF NOT EXISTS ix_movimentacoes_produto_filial_data
    ON movimentacoes (id_produto, id_filial, data_hora DESC);

-- ------------------------------------------------------------
-- 5. sp_EntradaEstoque  ->  sp_entrada_estoque()
--    Soma saldo, vincula prateleira e grava o Kardex.
-- ------------------------------------------------------------
CREATE OR REPLACE FUNCTION sp_entrada_estoque(
    p_id_produto    integer,
    p_id_filial     integer,
    p_id_prateleira integer,
    p_id_usuario    integer,
    p_quantidade    integer,
    p_observacao    varchar DEFAULT NULL
)
RETURNS TABLE (
    "idMovimentacao" integer,
    "idProduto"      integer,
    "idFilial"       integer,
    "idPrateleira"   integer,
    "saldoAnterior"  integer,
    "saldoAtual"     integer,
    "quantidade"     integer
)
LANGUAGE plpgsql
AS $$
DECLARE
    v_saldo_anterior integer;
    v_saldo_atual    integer;
    v_id_mov         integer;
BEGIN
    IF p_quantidade IS NULL OR p_quantidade <= 0 THEN
        RAISE EXCEPTION 'QUANTIDADE_INVALIDA';
    END IF;

    IF NOT EXISTS (SELECT 1 FROM produtos WHERE id_produto = p_id_produto) THEN
        RAISE EXCEPTION 'PRODUTO_NAO_ENCONTRADO';
    END IF;

    -- Trava a linha de saldo (cria se ainda não existir)
    SELECT ef.qtd_atual INTO v_saldo_anterior
      FROM estoquefilial ef
     WHERE ef.id_produto = p_id_produto
       AND ef.id_filial  = p_id_filial
       FOR UPDATE;

    IF NOT FOUND THEN
        INSERT INTO estoquefilial (id_produto, id_filial, qtd_atual, qtd_minima)
        VALUES (p_id_produto, p_id_filial, 0, 0);
        v_saldo_anterior := 0;
    END IF;

    v_saldo_atual := COALESCE(v_saldo_anterior, 0) + p_quantidade;

    UPDATE estoquefilial ef
       SET qtd_atual = v_saldo_atual
     WHERE ef.id_produto = p_id_produto
       AND ef.id_filial  = p_id_filial;

    -- Vincula o produto à prateleira informada, se ainda não estiver
    IF p_id_prateleira IS NOT NULL THEN
        INSERT INTO produtoprateleira (id_produto, id_prateleira, id_filial)
        SELECT p_id_produto, p_id_prateleira, p_id_filial
         WHERE NOT EXISTS (
               SELECT 1 FROM produtoprateleira pp
                WHERE pp.id_produto    = p_id_produto
                  AND pp.id_prateleira = p_id_prateleira
                  AND pp.id_filial     = p_id_filial);
    END IF;

    INSERT INTO movimentacoes
        (id_produto, id_filial, id_prateleira, id_usuario, tipo,
         quantidade, saldo_apos, data_hora, observacao, origem_scan)
    VALUES
        (p_id_produto, p_id_filial, p_id_prateleira, p_id_usuario, 'ENTRADA',
         p_quantidade, v_saldo_atual, NOW(), p_observacao, true)
    RETURNING id_movimentacao INTO v_id_mov;

    RETURN QUERY
    SELECT v_id_mov, p_id_produto, p_id_filial, p_id_prateleira,
           COALESCE(v_saldo_anterior, 0), v_saldo_atual, p_quantidade;
END;
$$;

-- ------------------------------------------------------------
-- 6. sp_SaidaPorPrateleira -> sp_saida_por_prateleira()
--    Baixa saldo a partir do código de barras da prateleira.
-- ------------------------------------------------------------
CREATE OR REPLACE FUNCTION sp_saida_por_prateleira(
    p_codigo_prateleira varchar,
    p_id_produto        integer,
    p_id_usuario        integer,
    p_quantidade        integer,
    p_observacao        varchar DEFAULT NULL,
    p_id_requisicao     integer DEFAULT NULL
)
RETURNS TABLE (
    "idMovimentacao" integer,
    "idProduto"      integer,
    "idFilial"       integer,
    "idPrateleira"   integer,
    "saldoAnterior"  integer,
    "saldoAtual"     integer,
    "quantidade"     integer
)
LANGUAGE plpgsql
AS $$
DECLARE
    v_id_prateleira  integer;
    v_id_filial      integer;
    v_saldo_anterior integer;
    v_saldo_atual    integer;
    v_id_mov         integer;
    v_observacao     varchar;
BEGIN
    IF p_quantidade IS NULL OR p_quantidade <= 0 THEN
        RAISE EXCEPTION 'QUANTIDADE_INVALIDA';
    END IF;

    SELECT pr.id_prateleira, pr.id_filial
      INTO v_id_prateleira, v_id_filial
      FROM prateleiras pr
     WHERE pr.codigo_barras = p_codigo_prateleira
       AND pr.ativo = true;

    IF NOT FOUND THEN
        RAISE EXCEPTION 'PRATELEIRA_NAO_ENCONTRADA';
    END IF;

    SELECT ef.qtd_atual INTO v_saldo_anterior
      FROM estoquefilial ef
     WHERE ef.id_produto = p_id_produto
       AND ef.id_filial  = v_id_filial
       FOR UPDATE;

    IF NOT FOUND OR COALESCE(v_saldo_anterior, 0) < p_quantidade THEN
        RAISE EXCEPTION 'SALDO_INSUFICIENTE';
    END IF;

    v_saldo_atual := v_saldo_anterior - p_quantidade;

    UPDATE estoquefilial ef
       SET qtd_atual = v_saldo_atual
     WHERE ef.id_produto = p_id_produto
       AND ef.id_filial  = v_id_filial;

    v_observacao := p_observacao;
    IF p_id_requisicao IS NOT NULL THEN
        v_observacao := COALESCE(v_observacao, '') || ' [REQ #' || p_id_requisicao || ']';
    END IF;

    INSERT INTO movimentacoes
        (id_produto, id_filial, id_prateleira, id_usuario, tipo,
         quantidade, saldo_apos, data_hora, observacao, origem_scan)
    VALUES
        (p_id_produto, v_id_filial, v_id_prateleira, p_id_usuario, 'SAIDA',
         p_quantidade, v_saldo_atual, NOW(), v_observacao, true)
    RETURNING id_movimentacao INTO v_id_mov;

    RETURN QUERY
    SELECT v_id_mov, p_id_produto, v_id_filial, v_id_prateleira,
           v_saldo_anterior, v_saldo_atual, p_quantidade;
END;
$$;

-- ------------------------------------------------------------
-- 7. sp_Kardex -> sp_kardex()
--    Extrato de movimentações por filial/produto/período.
-- ------------------------------------------------------------
CREATE OR REPLACE FUNCTION sp_kardex(
    p_id_filial   integer,
    p_id_produto  integer   DEFAULT NULL,
    p_data_inicio timestamp DEFAULT NULL,
    p_data_fim    timestamp DEFAULT NULL
)
RETURNS TABLE (
    "idMovimentacao" integer,
    "idProduto"      integer,
    "produto"        varchar,
    "codigoSku"      varchar,
    "unidade"        varchar,
    "idFilial"       integer,
    "filial"         varchar,
    "idPrateleira"   integer,
    "prateleira"     varchar,
    "idUsuario"      integer,
    "usuario"        varchar,
    "tipo"           varchar,
    "quantidade"     integer,
    "saldoApos"      integer,
    "observacao"     varchar,
    "origemScan"     boolean,
    "dataHora"       timestamp
)
LANGUAGE sql
STABLE
AS $$
    SELECT
        m.id_movimentacao,
        m.id_produto,
        p.nome,
        p.codigo_sku,
        p.unidade,
        m.id_filial,
        f.nome,
        m.id_prateleira,
        pr.codigo_barras,
        m.id_usuario,
        u.nome,
        m.tipo,
        m.quantidade,
        m.saldo_apos,
        m.observacao,
        m.origem_scan,
        m.data_hora
      FROM movimentacoes m
      INNER JOIN produtos p ON p.id_produto = m.id_produto
      INNER JOIN filiais  f ON f.id_filial  = m.id_filial
      LEFT  JOIN prateleiras pr ON pr.id_prateleira = m.id_prateleira
      LEFT  JOIN usuarios    u  ON u.id_usuario     = m.id_usuario
     WHERE m.id_filial = p_id_filial
       AND (p_id_produto  IS NULL OR m.id_produto = p_id_produto)
       AND (p_data_inicio IS NULL OR m.data_hora >= p_data_inicio)
       AND (p_data_fim    IS NULL OR m.data_hora <  p_data_fim + interval '1 day')
     ORDER BY m.data_hora DESC, m.id_movimentacao DESC;
$$;
