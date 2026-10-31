export function ArchivedBadge({ label }: { label: string }) {
  return (
    <span className="rounded-full bg-muted px-2 py-0.5 text-xs text-muted-foreground">
      {label}
    </span>
  )
}
