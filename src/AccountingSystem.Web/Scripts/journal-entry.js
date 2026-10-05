/*
 * journal-entry.js
 * ---------------------------------------------------------------------------
 * Client behaviour for the journal entry screens:
 *   - the dynamic debit/credit grid on Create.cshtml (add/remove lines,
 *     live totals and a balance indicator);
 *   - the DataTables grid + approve/post/void actions on Index.cshtml.
 *
 * The browser-side balance check is a usability aid only. The server
 * re-validates every rule (JournalController.Create and JournalService) and the
 * database enforces it again via triggers and gl.usp_PostJournalEntry, so a
 * user cannot bypass it by editing the page.
 */
(function ($) {
    'use strict';

    // =====================================================================
    // Create screen: dynamic line grid
    // =====================================================================
    function initJournalCreate() {
        var $body = $('#linesBody');
        if ($body.length === 0) return;

        var $template = $('#lineTemplate');
        var nextIndex = $body.find('tr.journal-line').length;

        // Re-index the name attributes so the model binder receives a
        // contiguous Lines[0..n] array after rows are added or removed.
        function reindex() {
            $body.find('tr.journal-line').each(function (rowIndex) {
                $(this).find('[name]').each(function () {
                    var name = $(this).attr('name');
                    $(this).attr('name', name.replace(/Lines\[\d+\]/, 'Lines[' + rowIndex + ']'));
                });
            });
            nextIndex = $body.find('tr.journal-line').length;
        }

        // Recompute totals and toggle the balance badge.
        function recalculate() {
            var totalDebit = 0;
            var totalCredit = 0;

            $body.find('tr.journal-line').each(function () {
                var debit = parseFloat($(this).find('.debit-input').val()) || 0;
                var credit = parseFloat($(this).find('.credit-input').val()) || 0;
                totalDebit += debit;
                totalCredit += credit;
            });

            $('#totalDebit').text(totalDebit.toFixed(4));
            $('#totalCredit').text(totalCredit.toFixed(4));

            var $status = $('#balanceStatus');
            var balanced = totalDebit === totalCredit && totalDebit > 0;
            $status
                .removeClass('bg-secondary bg-success bg-danger')
                .addClass(balanced ? 'bg-success' : (totalDebit > 0 ? 'bg-danger' : 'bg-secondary'))
                .text(balanced
                    ? 'Balanced'
                    : 'Not balanced (difference ' + Math.abs(totalDebit - totalCredit).toFixed(4) + ')');
        }

        // Enforce "one side per line": entering a debit clears the credit and
        // vice versa, matching the CK_JournalDetail_Amounts constraint.
        $body.on('input', '.debit-input', function () {
            if (parseFloat($(this).val()) > 0) $(this).closest('tr').find('.credit-input').val('0');
            recalculate();
        });

        $body.on('input', '.credit-input', function () {
            if (parseFloat($(this).val()) > 0) $(this).closest('tr').find('.debit-input').val('0');
            recalculate();
        });

        // Add a line by cloning the template.
        $('#addLine').on('click', function () {
            var html = $template.html().replace(/__INDEX__/g, nextIndex);
            $body.append(html);
            nextIndex++;
            recalculate();
        });

        // Remove a line, keeping at least two rows.
        $body.on('click', '.remove-line', function () {
            if ($body.find('tr.journal-line').length <= 2) {
                window.alert('A journal entry requires at least two lines.');
                return;
            }
            $(this).closest('tr').remove();
            reindex();
            recalculate();
        });

        // Block submission of an unbalanced entry before it reaches the server.
        $('#journalForm').on('submit', function (e) {
            recalculate();
            var totalDebit = parseFloat($('#totalDebit').text()) || 0;
            var totalCredit = parseFloat($('#totalCredit').text()) || 0;

            if (totalDebit !== totalCredit || totalDebit === 0) {
                e.preventDefault();
                window.alert('The journal entry must balance before it can be saved. ' +
                             'Debits ' + totalDebit.toFixed(4) + ' vs credits ' + totalCredit.toFixed(4) + '.');
            }
        });

        recalculate();
    }

    // =====================================================================
    // Index screen: DataTables + actions
    // =====================================================================
    function antiForgeryToken() {
        return $('input[name="__RequestVerificationToken"]').val();
    }

    function initJournalIndex() {
        var table = $('#journalTable');
        if (table.length === 0 || !window.journalConfig) return;

        var config = window.journalConfig;

        var dt = table.DataTable({
            serverSide: true,
            processing: true,
            ajax: {
                url: config.listUrl,
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
                {
                    data: 'Status',
                    render: function (status) {
                        var map = {
                            Draft: 'secondary', Approved: 'info',
                            Posted: 'success', Void: 'danger'
                        };
                        return '<span class="badge bg-' + (map[status] || 'secondary') + '">' + status + '</span>';
                    }
                },
                { data: 'TotalDebit', className: 'text-end', render: money },
                { data: 'TotalCredit', className: 'text-end', render: money },
                { data: 'PostedBy', defaultContent: '' },
                {
                    data: 'JournalId',
                    orderable: false,
                    className: 'text-end',
                    render: function (id, type, row) {
                        var html = '<a class="btn btn-sm btn-outline-secondary" href="' +
                                   config.detailsUrl + '/' + id + '">View</a> ';
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

        function money(value) {
            var n = parseFloat(value) || 0;
            return n.toLocaleString(undefined, { minimumFractionDigits: 4, maximumFractionDigits: 4 });
        }

        $('#statusFilter').on('change', function () { dt.ajax.reload(); });
        $('#searchFilter').on('keyup', debounce(function () { dt.ajax.reload(); }, 300));

        // Approve / Post / Void via AJAX, with the anti-forgery token.
        table.on('click', '.action', function () {
            var $btn = $(this);
            var action = $btn.data('action');
            var id = $btn.data('id');
            var url = config[action + 'Url'];
            var payload = { id: id, __RequestVerificationToken: antiForgeryToken() };

            if (action === 'void') {
                var reason = window.prompt('Reason for voiding this entry:');
                if (!reason) return;
                payload.reason = reason;
            }

            $btn.prop('disabled', true);
            $.ajax({
                url: url,
                type: 'POST',
                data: payload
            }).done(function (response) {
                if (response && response.success) {
                    dt.ajax.reload(null, false);
                    window.alert(response.message || 'Done.');
                } else {
                    window.alert((response && response.message) || 'The operation failed.');
                }
            }).fail(function (xhr) {
                var message = (xhr.responseJSON && xhr.responseJSON.message) || 'The operation failed.';
                window.alert(message);
            }).always(function () {
                $btn.prop('disabled', false);
            });
        });
    }

    function debounce(fn, wait) {
        var timer;
        return function () {
            var context = this, args = arguments;
            clearTimeout(timer);
            timer = setTimeout(function () { fn.apply(context, args); }, wait);
        };
    }

    $(function () {
        initJournalCreate();
        initJournalIndex();
    });
})(jQuery);
