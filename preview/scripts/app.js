/*
 * app.js - live-preview behaviour.
 *
 * Talks to the live preview server (/api/journal/*), which executes the same
 * double-entry rules as the real JournalService. The journal list is fed by the
 * server-side DataTables endpoint, and approve/post/void actually change state.
 */
(function ($) {
    'use strict';

    function money(value) {
        var n = parseFloat(value) || 0;
        return n.toLocaleString(undefined, { minimumFractionDigits: 4, maximumFractionDigits: 4 });
    }

    function statusBadge(status) {
        var map = { Draft: 'secondary', Approved: 'info', Posted: 'success', Void: 'danger' };
        return '<span class="badge bg-' + (map[status] || 'secondary') + '">' + status + '</span>';
    }

    // ------------------------------------------------------------------
    // Journal list: server-side DataTables
    // ------------------------------------------------------------------
    function initJournalIndex() {
        var table = $('#journalTable');
        if (table.length === 0) return;

        var dt = table.DataTable({
            serverSide: true,
            processing: true,
            ajax: {
                url: '/api/journal/list',
                type: 'GET',
                data: function (d) {
                    d.status = $('#statusFilter').val();
                    d.search = $('#searchFilter').val();
                }
            },
            order: [[1, 'desc']],
            columns: [
                { data: 'VoucherNumber' },
                { data: 'TransactionDate' },
                { data: 'Reference', defaultContent: '' },
                { data: 'Status', render: statusBadge },
                { data: 'TotalDebit', className: 'text-end', render: money },
                { data: 'TotalCredit', className: 'text-end', render: money },
                { data: 'PostedBy', defaultContent: '' },
                {
                    data: 'JournalId',
                    orderable: false,
                    className: 'text-end',
                    render: function (id, type, row) {
                        var html = '<a class="btn btn-sm btn-outline-secondary" href="/journal/details.html?id=' +
                                   id + '">View</a> ';
                        if (row.Status === 'Draft') {
                            html += '<button class="btn btn-sm btn-outline-info action" data-action="approve" data-id="' + id + '">Approve</button> ';
                        }
                        if (row.Status === 'Draft' || row.Status === 'Approved') {
                            html += '<button class="btn btn-sm btn-outline-success action" data-action="post" data-id="' + id + '">Post</button> ';
                        }
                        if (row.Status === 'Posted') {
                            html += '<button class="btn btn-sm btn-outline-danger action" data-action="void" data-id="' + id + '">Void</button>';
                        }
                        return html;
                    }
                }
            ]
        });

        $('#statusFilter').on('change', function () { dt.ajax.reload(); });
        $('#searchFilter').on('keyup', debounce(function () { dt.ajax.reload(); }, 300));

        table.on('click', '.action', function () {
            var $btn = $(this);
            var action = $btn.data('action');
            var id = $btn.data('id');
            var payload = { id: id };

            if (action === 'void') {
                var reason = window.prompt('Reason for voiding this entry:');
                if (!reason) return;
                payload.reason = reason;
            }

            $btn.prop('disabled', true);
            $.ajax({
                url: '/api/journal/' + action,
                type: 'POST',
                contentType: 'application/json',
                data: JSON.stringify(payload)
            }).done(function (response) {
                if (response && response.success) {
                    dt.ajax.reload(null, false);
                    toast(response.message || 'Done.', 'success');
                } else {
                    toast((response && response.message) || 'The operation failed.', 'danger');
                }
            }).fail(function (xhr) {
                var message = (xhr.responseJSON && xhr.responseJSON.message) || 'The operation failed.';
                toast(message, 'danger');
            }).always(function () {
                $btn.prop('disabled', false);
            });
        });
    }

    function toast(message, kind) {
        var $host = $('#formAlert');
        if ($host.length === 0) {
            $host = $('<div id="formAlert"></div>').insertAfter('h2').first();
        }
        $host.html('<div class="alert alert-' + kind + ' alert-dismissible fade show">' +
                   message + '<button type="button" class="btn-close" data-bs-dismiss="alert"></button></div>');
        window.scrollTo(0, 0);
    }

    function debounce(fn, wait) {
        var timer;
        return function () {
            var context = this, args = arguments;
            clearTimeout(timer);
            timer = setTimeout(function () { fn.apply(context, args); }, wait);
        };
    }

    // ------------------------------------------------------------------
    // Create screen: dynamic grid + live save
    // ------------------------------------------------------------------
    function initJournalCreate() {
        var $body = $('#linesBody');
        if ($body.length === 0) return;

        var $template = $('#lineTemplate');

        function recalculate() {
            var totalDebit = 0, totalCredit = 0;
            $body.find('tr.journal-line').each(function () {
                totalDebit += parseFloat($(this).find('.debit-input').val()) || 0;
                totalCredit += parseFloat($(this).find('.credit-input').val()) || 0;
            });
            $('#totalDebit').text(totalDebit.toFixed(4));
            $('#totalCredit').text(totalCredit.toFixed(4));

            var $status = $('#balanceStatus');
            var balanced = totalDebit === totalCredit && totalDebit > 0;
            $status.removeClass('bg-secondary bg-success bg-danger')
                .addClass(balanced ? 'bg-success' : (totalDebit > 0 ? 'bg-danger' : 'bg-secondary'))
                .text(balanced ? 'Balanced'
                    : 'Not balanced (difference ' + Math.abs(totalDebit - totalCredit).toFixed(4) + ')');
        }

        $body.on('input', '.debit-input', function () {
            if (parseFloat($(this).val()) > 0) $(this).closest('tr').find('.credit-input').val('0');
            recalculate();
        });
        $body.on('input', '.credit-input', function () {
            if (parseFloat($(this).val()) > 0) $(this).closest('tr').find('.debit-input').val('0');
            recalculate();
        });

        $('#addLine').on('click', function () {
            $body.append($template.html());
            recalculate();
        });

        $body.on('click', '.remove-line', function () {
            if ($body.find('tr.journal-line').length <= 2) {
                toast('A journal entry requires at least two lines.', 'warning');
                return;
            }
            $(this).closest('tr').remove();
            recalculate();
        });

        $('#journalForm').on('submit', function (e) {
            e.preventDefault();
            recalculate();

            var lines = [];
            $body.find('tr.journal-line').each(function () {
                var accountId = parseInt($(this).find('.account-select').val(), 10);
                var debit = parseFloat($(this).find('.debit-input').val()) || 0;
                var credit = parseFloat($(this).find('.credit-input').val()) || 0;
                if (accountId && (debit || credit)) {
                    lines.push({
                        accountId: accountId,
                        debit: debit,
                        credit: credit,
                        description: $(this).find('input[name="description"]').val()
                    });
                }
            });

            var payload = {
                voucherNumber: $('#VoucherNumber').val(),
                transactionDate: $('#TransactionDate').val(),
                reference: $('#Reference').val(),
                description: $('#Description').val(),
                lines: lines
            };

            $.ajax({
                url: '/api/journal/create',
                type: 'POST',
                contentType: 'application/json',
                data: JSON.stringify(payload)
            }).done(function (response) {
                if (response && response.success) {
                    window.location.href = '/journal/details.html?id=' + response.id;
                } else {
                    toast((response && response.message) || 'The entry could not be saved.', 'danger');
                }
            }).fail(function (xhr) {
                var message = (xhr.responseJSON && xhr.responseJSON.message) || 'The entry could not be saved.';
                toast(message, 'danger');
            });
        });

        recalculate();
    }

    $(function () {
        initJournalIndex();
        initJournalCreate();
        // Bound with jQuery rather than an inline onclick, matching the rest of
        // the client code (and avoiding any inline-handler CSP issues).
        jQuery('#auditTamper').on('click', tryTamper);
    });
})(jQuery);

// Demonstrates that the audit trail is append-only: the server always refuses.
function tryTamper() {
    jQuery.ajax({
        url: '/api/audit/tamper',
        type: 'POST',
        contentType: 'application/json',
        data: JSON.stringify({ id: 1 })
    }).fail(function (xhr) {
        var message = (xhr.responseJSON && xhr.responseJSON.message)
            || 'The audit log is append-only and cannot be modified or deleted.';
        jQuery('#tamperResult').html(
            '<div class="alert alert-danger py-2">Refused: ' + message + '</div>');
    }).done(function () {
        jQuery('#tamperResult').html('<div class="alert alert-warning py-2">Unexpected: the write was allowed.</div>');
    });
}
