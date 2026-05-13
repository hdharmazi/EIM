// Please see documentation at https://learn.microsoft.com/aspnet/core/client-side/bundling-and-minification
// for details on configuring this project to bundle and minify static web assets.

document.addEventListener('DOMContentLoaded', function () {
    const toggle  = document.getElementById('sidebarToggle');
    const overlay = document.getElementById('sidebarOverlay');
    const COLLAPSED_KEY = 'sidebarCollapsed';

    // Restore desktop collapsed state on page load
    if (window.innerWidth > 768 && localStorage.getItem(COLLAPSED_KEY) === 'true') {
        document.body.classList.add('sidebar-collapsed');
    }

    if (toggle) {
        toggle.addEventListener('click', function () {
            if (window.innerWidth <= 768) {
                // Mobile: slide in/out with overlay
                document.body.classList.toggle('sidebar-open');
            } else {
                // Desktop: collapse / expand
                const collapsed = document.body.classList.toggle('sidebar-collapsed');
                localStorage.setItem(COLLAPSED_KEY, collapsed);
            }
        });
    }

    // Close sidebar when overlay is tapped on mobile
    if (overlay) {
        overlay.addEventListener('click', function () {
            document.body.classList.remove('sidebar-open');
        });
    }

    // Close sidebar on navigation on mobile
    document.querySelectorAll('.sidebar-link').forEach(function (link) {
        link.addEventListener('click', function () {
            if (window.innerWidth <= 768) {
                document.body.classList.remove('sidebar-open');
            }
        });
    });
});
