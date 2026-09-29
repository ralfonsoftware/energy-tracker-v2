import { render, screen } from '@testing-library/react'
import { describe, expect, it } from 'vitest'
import { EntryGrid, EntryTile } from './entry-grid'

describe('EntryGrid', () => {
  it('renders each tile as a list item inside an auto-fill minmax(260px) grid', () => {
    render(
      <EntryGrid>
        <EntryTile>one</EntryTile>
        <EntryTile>two</EntryTile>
        <EntryTile>three</EntryTile>
      </EntryGrid>,
    )
    expect(screen.getAllByRole('listitem')).toHaveLength(3)
    expect(screen.getByRole('list').className).toContain('grid-cols-[repeat(auto-fill,minmax(260px,1fr))]')
  })

  it('renders the actions row only when actions are given', () => {
    render(
      <EntryGrid>
        <EntryTile actions={<button>Edit</button>}>with</EntryTile>
        <EntryTile>without</EntryTile>
      </EntryGrid>,
    )
    expect(screen.getAllByRole('button')).toHaveLength(1)
  })
})
